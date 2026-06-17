using AutoSwitchKVM.App.Services;
using AutoSwitchKVM.App.Support;
using AutoSwitchKVM.Core;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AutoSwitchKVM.App;

/// Tray-only application. Owns the AppController and a system-tray icon whose context menu is rebuilt
/// from live state each time it opens (status, profiles, devices, quick actions). Left-click opens
/// the Settings window.
public partial class App : Application
{
    public static AppController Controller { get; private set; } = null!;

    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private DispatcherQueue? _dispatcher;
    private readonly Dictionary<string, ImageSource> _iconCache = new();
    private string? _currentIconAsset;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Controller = new AppController();
        Controller.Start();

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
        menu.Opening += (_, _) => BuildTrayMenu(menu);
        _trayIcon.ContextFlyout = menu;
        _trayIcon.LeftClickCommand = new RelayCommand(ShowSettings);
        _trayIcon.DoubleClickCommand = new RelayCommand(ShowSettings);
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
                var item = new ToggleMenuFlyoutItem { Text = p.Name, IsChecked = id == c.Config.ActiveProfileID };
                item.Click += (_, _) => c.SwitchProfile(id);
                menu.Items.Add(item);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        foreach (var d in c.Config.Devices)
        {
            var dev = d;
            var item = new MenuFlyoutItem { Text = $"{dev.Name}  -  {c.StatusFor(dev).Label}" };
            item.Click += async (_, _) =>
            {
                if (c.StatusFor(dev).Kind == DeviceStatusKind.Connected) await c.TestDisconnectAsync(dev);
                else await c.TestConnectAsync(dev);
            };
            menu.Items.Add(item);
        }
        if (c.Config.Devices.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());

        var connectAll = new MenuFlyoutItem { Text = "Connect all" };
        connectAll.Click += async (_, _) => await c.ConnectAllAsync();
        menu.Items.Add(connectAll);

        var disconnectAll = new MenuFlyoutItem { Text = "Disconnect all" };
        disconnectAll.Click += async (_, _) => await c.DisconnectAllAsync();
        menu.Items.Add(disconnectAll);

        var pause = new ToggleMenuFlyoutItem { Text = "Pause automation", IsChecked = c.Paused };
        pause.Click += (_, _) => c.TogglePause();
        menu.Items.Add(pause);

        menu.Items.Add(new MenuFlyoutSeparator());

        var settings = new MenuFlyoutItem { Text = "Settings..." };
        settings.Click += (_, _) => ShowSettings();
        menu.Items.Add(settings);

        var exit = new MenuFlyoutItem { Text = "Exit" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);
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

    private void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    private void ExitApp()
    {
        Controller.Shutdown();
        _trayIcon?.Dispose();
        _trayIcon = null;
        Current.Exit();
    }
}
