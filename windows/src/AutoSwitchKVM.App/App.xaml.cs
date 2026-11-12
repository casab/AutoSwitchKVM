using AutoSwitchKVM.App.Services;
using AutoSwitchKVM.App.Support;
using AutoSwitchKVM.App.ViewModels;
using AutoSwitchKVM.Core;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppLifecycle;

namespace AutoSwitchKVM.App;

/// Tray-only application. Owns the AppController and a system-tray icon whose context menu is rebuilt
/// from live state each time it opens (status, profiles, devices, quick actions). Left-click opens
/// the Settings window.
public partial class App : Application
{
    private const string InstanceKey = "AutoSwitchKVM-main";

    public static AppController Controller { get; private set; } = null!;

    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private DispatcherQueue? _dispatcher;
    private readonly Dictionary<string, ImageSource> _iconCache = new();
    private string? _currentIconAsset;

    public App()
    {
        InitializeComponent();

        // Tray-only lifetime: without this, WinUI's default OnLastWindowClose shuts the whole
        // process down the first time the user closes the Settings window. Exit only via the
        // tray "Exit" item.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // Global safety nets: a stray exception (Bluetooth callback, WMI hiccup) must not take
        // the tray app down — the reconcile/poll timers recover state.
        UnhandledException += (_, e) =>
        {
            Log.Error("app", "unhandled exception", e.Exception);
            e.Handled = true;
        };
        // XAML diagnostics into the app log: a missing resource key or a failed binding otherwise
        // only shows as a generic "XAML parsing failed" or silently empty UI.
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, e) => Log.Warn("xaml", $"resource reference failed: {e.Message}");
        DebugSettings.IsBindingTracingEnabled = true;
        DebugSettings.BindingFailed += (_, e) => Log.Debug("xaml", $"binding failed: {e.Message}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("app", "unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: two engines would fight over the radio, the devices, and the config
        // file. Redirect this activation to the existing instance (which reacts by showing
        // Settings) and bail out.
        // Developer preview (--ui-preview [--settings[=page]] [--panel]): the UI against the real
        // config, but no engine/monitors and no writes, under its own instance key so it can run
        // next to the real app (screenshots, design work).
        var cmd = Environment.GetCommandLineArgs();
        var preview = cmd.Contains("--ui-preview");

        var mainInstance = AppInstance.FindOrRegisterForKey(preview ? InstanceKey + "-preview" : InstanceKey);
        if (!mainInstance.IsCurrent)
        {
            Log.Info("app", "another instance is already running; redirecting activation and exiting");
            try
            {
                mainInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs())
                    .AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                Log.Warn("app", $"activation redirect failed: {ex.Message}");
            }
            Exit();
            return;
        }
        mainInstance.Activated += (_, _) => _dispatcher?.TryEnqueue(ShowSettings);

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        var previewConfig = preview ? cmd.FirstOrDefault(a => a.StartsWith("--config="))?["--config=".Length..] : null;
        Controller = new AppController(preview, previewConfig, demo: cmd.Contains("--preview-demo"));
        if (!preview) Controller.Start();
        AppViewModel.Initialize(Controller);

        if (preview)
        {
            Log.Info("app", "UI preview mode: engine not started, nothing is saved");
            var page = cmd.FirstOrDefault(a => a.StartsWith("--settings"))?.Split('=').ElementAtOrDefault(1);
            if (cmd.Any(a => a.StartsWith("--settings"))) ShowSettings(page ?? "overview");
            if (cmd.Contains("--panel")) Panel.ShowPanel();
            return;
        }

        _currentIconAsset = Controller.TrayIconAsset;
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = $"AutoSwitch KVM - {Controller.StatusText}",
            IconSource = IconFor(_currentIconAsset),
            // Native Win32 popup menu: renders reliably (the XAML "SecondWindow" flyout showed a
            // blank box because the secondary window doesn't inherit the app theme).
            ContextMenuMode = ContextMenuMode.PopupMenu,
        };

        var menu = new MenuFlyout();
        // PopupMenu mode never raises MenuFlyout.Opening (the flyout is converted to a native
        // Win32 menu), so rebuild on the right-click itself; keep the Opening hook as well in
        // case the context-menu mode ever changes.
        menu.Opening += (_, _) => BuildTrayMenu(menu);
        _trayIcon.ContextFlyout = menu;
        // Left-click: the quick panel; double-click: Settings. (Right-click keeps the native menu.)
        _trayIcon.LeftClickCommand = new RelayCommand(() => Panel.Toggle());
        _trayIcon.DoubleClickCommand = new RelayCommand(ShowSettings);
        _trayIcon.RightClickCommand = new RelayCommand(() => BuildTrayMenu(menu));
        BuildTrayMenu(menu);   // populate up front so the popup has items on first open
        _trayIcon.ForceCreate();

        // Swap the tray icon (and tooltip) to match state changes (active / inactive / paused / off).
        Controller.StateChanged += () => _dispatcher?.TryEnqueue(UpdateTrayIcon);
    }

    private void UpdateTrayIcon()
    {
        if (_trayIcon is null) return;
        _trayIcon.ToolTipText = $"AutoSwitch KVM - {Controller.StatusText}";
        var asset = Controller.TrayIconAsset;
        if (asset == _currentIconAsset) return;
        _currentIconAsset = asset;
        _trayIcon.IconSource = IconFor(asset);
    }

    /// Items are wired via Command (not Click): in ContextMenuMode.PopupMenu, H.NotifyIcon
    /// converts the flyout into a native Win32 menu and dispatches selections by executing the
    /// item's ICommand — Click handlers are never raised there.
    private void BuildTrayMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        var c = Controller;

        menu.Items.Add(new MenuFlyoutItem { Text = c.StatusText, IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());

        if (c.Config.Profiles.Count > 1)
        {
            foreach (var p in c.Config.Profiles)
            {
                var id = p.Id;
                menu.Items.Add(new ToggleMenuFlyoutItem
                {
                    Text = p.Name,
                    IsChecked = id == c.Config.ActiveProfileID,
                    Command = new RelayCommand(() => c.SwitchProfile(id)),
                });
            }
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        foreach (var d in c.Config.Devices)
        {
            var dev = d;
            menu.Items.Add(new MenuFlyoutItem
            {
                Text = $"{dev.Name}  -  {c.StatusFor(dev).Label}",
                // Never throws: the controller's per-device actions log their own failures.
                Command = new RelayCommand(() =>
                {
                    _ = c.StatusFor(dev).Kind == DeviceStatusKind.Connected
                        ? c.ReleaseDeviceAsync(dev)
                        : c.ConnectDeviceAsync(dev);
                }),
            });
        }
        if (c.Config.Devices.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Connect all",
            Command = new RelayCommand(c.ConnectAll),
        });
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Disconnect all",
            Command = new RelayCommand(c.DisconnectAll),
        });
        menu.Items.Add(new ToggleMenuFlyoutItem
        {
            Text = "Pause automation",
            IsChecked = c.Paused,
            Command = new RelayCommand(c.TogglePause),
        });

        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Settings...",
            Command = new RelayCommand(ShowSettings),
        });
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Exit",
            Command = new RelayCommand(ExitApp),
        });
    }

    /// Cached tray icon (monitor glyph matching the macOS menu-bar symbol) for the given asset.
    /// Falls back to a generated letter if the asset can't be loaded.
    private ImageSource IconFor(string asset)
    {
        if (_iconCache.TryGetValue(asset, out var cached)) return cached;
        ImageSource image;
        try { image = new BitmapImage(new Uri($"ms-appx:///Assets/{asset}")); }
        catch (Exception ex)
        {
            Log.Warn("app", $"tray icon '{asset}' load failed, using fallback: {ex.Message}");
            image = new GeneratedIconSource { Text = "K", Foreground = new SolidColorBrush(Colors.White) };
        }
        _iconCache[asset] = image;
        return image;
    }

    private void ShowSettings() => ShowSettings(null);

    private void ShowSettings(string? page)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        if (page is not null) _settingsWindow.Navigate(page);
        _settingsWindow.Activate();
    }

    /// The tray quick panel, created on first use and then shown/hidden.
    private TrayPanel Panel
    {
        get
        {
            if (_panel is null)
            {
                _panel = new TrayPanel();
                _panel.SettingsRequested += ShowSettings;
            }
            return _panel;
        }
    }
    private TrayPanel? _panel;

    private void ExitApp()
    {
        Controller.Shutdown();
        _trayIcon?.Dispose();
        _trayIcon = null;
        Current.Exit();
    }
}
