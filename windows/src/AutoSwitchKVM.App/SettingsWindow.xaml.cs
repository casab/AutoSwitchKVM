using AutoSwitchKVM.App.Platform;
using AutoSwitchKVM.App.Support;
using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace AutoSwitchKVM.App;

/// Settings window with five tabs (Source / Devices / General / Extras / Diagnostics), mirroring the
/// macOS app. Built in code-behind against App.Controller. Dynamic panels (devices, diagnostics, log)
/// refresh on AppController.StateChanged / Log.Changed.
public sealed partial class SettingsWindow : Window
{
    private static Services.AppController C => App.Controller;

    // Source tab fields
    private TextBlock _srcCurrent = null!;
    private TextBox _srcName = null!;
    private TextBox _srcVendor = null!;
    private TextBox _srcPids = null!;

    // Devices tab
    private StackPanel _devicesPanel = null!;

    // Diagnostics tab
    private TextBlock _diagText = null!;
    private TextBox _logBox = null!;

    // General tab - shortcut combo labels (updated after record/restore)
    private readonly Dictionary<HotKeyAction, TextBlock> _shortcutLabels = new();

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "AutoSwitch KVM - Settings";
        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(720, 640)); } catch { /* sizing is best-effort */ }

        var tabs = new TabView { IsAddTabButtonVisible = false, CanReorderTabs = false, CanDragTabs = false };
        tabs.TabItems.Add(new TabViewItem { Header = "Source", IsClosable = false, Content = BuildSourceTab() });
        tabs.TabItems.Add(new TabViewItem { Header = "Devices", IsClosable = false, Content = BuildDevicesTab() });
        tabs.TabItems.Add(new TabViewItem { Header = "General", IsClosable = false, Content = BuildGeneralTab() });
        tabs.TabItems.Add(new TabViewItem { Header = "Extras", IsClosable = false, Content = BuildExtrasTab() });
        tabs.TabItems.Add(new TabViewItem { Header = "Diagnostics", IsClosable = false, Content = BuildDiagnosticsTab() });
        Root.Children.Add(tabs);

        C.StateChanged += OnStateChanged;
        Log.Changed += OnLogChanged;
        Closed += (_, _) =>
        {
            C.StateChanged -= OnStateChanged;
            Log.Changed -= OnLogChanged;
        };
    }

    private void OnStateChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        RefreshSource();
        RefreshDevices();
        RefreshDiagnostics();
    });

    private void OnLogChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_logBox != null) _logBox.Text = Log.PlainText();
    });

    // ===================== Source =====================

    private UIElement BuildSourceTab()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(16) };
        panel.Children.Add(Header("Current source"));
        _srcCurrent = new TextBlock { TextWrapping = TextWrapping.WrapWholeWords };
        panel.Children.Add(_srcCurrent);

        panel.Children.Add(Header("Edit source (one vendor + product IDs)"));
        _srcName = new TextBox { Header = "Name", PlaceholderText = "e.g. Desk KVM" };
        _srcVendor = new TextBox { Header = "Vendor ID (hex)", PlaceholderText = "05E3" };
        _srcPids = new TextBox { Header = "Product IDs (hex, comma-separated)", PlaceholderText = "0626, 0610" };
        panel.Children.Add(_srcName);
        panel.Children.Add(_srcVendor);
        panel.Children.Add(_srcPids);

        var save = new Button { Content = "Save source" };
        save.Click += (_, _) => SaveSource();
        var learn = new Button { Content = "Learn source..." };
        learn.Click += async (_, _) => await LearnSourceAsync();
        panel.Children.Add(Row(save, learn));

        RefreshSource();
        return new ScrollViewer { Content = panel };
    }

    private void RefreshSource()
    {
        if (_srcCurrent is null) return;
        var s = C.Config.Source;
        _srcCurrent.Text = s is null ? "(no source set)" : $"{s.Name}    {s.DisplayVidPid}";
        if (s != null)
        {
            if (string.IsNullOrEmpty(_srcName.Text)) _srcName.Text = s.Name;
            if (string.IsNullOrEmpty(_srcVendor.Text)) _srcVendor.Text = s.VendorID.ToString("X4");
            if (string.IsNullOrEmpty(_srcPids.Text))
                _srcPids.Text = string.Join(", ", s.ProductIDs.OrderBy(p => p).Select(p => p.ToString("X4")));
        }
    }

    private void SaveSource()
    {
        if (!TryParseHex(_srcVendor.Text, out var vendor)) return;
        var pids = new HashSet<ushort>();
        foreach (var part in _srcPids.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (TryParseHex(part, out var pid)) pids.Add(pid);
        if (pids.Count == 0) return;

        var name = string.IsNullOrWhiteSpace(_srcName.Text) ? "Source" : _srcName.Text.Trim();
        C.Mutate(cfg => cfg.Source = new USBSource { Name = name, VendorID = vendor, ProductIDs = pids },
            refreshMonitoring: true, reevaluate: true);
    }

    private async Task LearnSourceAsync()
    {
        var learner = C.CreateLearner();
        learner.Start();

        var count = new TextBlock { Text = "Changes detected: 0" };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = "Switch your KVM to the other computer and back, then click Done.",
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        body.Children.Add(count);

        void OnLearn() => DispatcherQueue.TryEnqueue(() => count.Text = $"Changes detected: {learner.ChangeCount}");
        learner.Changed += OnLearn;

        var dialog = new ContentDialog
        {
            Title = "Learn source",
            Content = body,
            PrimaryButtonText = "Done",
            CloseButtonText = "Cancel",
            XamlRoot = Root.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        learner.Changed -= OnLearn;

        if (result != ContentDialogResult.Primary) { learner.Cancel(); return; }

        var candidates = learner.Finish();
        if (candidates.Count == 0) return;

        // Prefer the vendor with the most changed product IDs (the KVM hub).
        var vendor = candidates.GroupBy(c => c.VendorID).OrderByDescending(g => g.Count()).First();
        _srcVendor.Text = vendor.Key.ToString("X4");
        _srcPids.Text = string.Join(", ", vendor.Select(c => c.ProductID.ToString("X4")));
        if (string.IsNullOrWhiteSpace(_srcName.Text)) _srcName.Text = "KVM";
    }

    // ===================== Devices =====================

    private UIElement BuildDevicesTab()
    {
        var outer = new StackPanel { Spacing = 10, Margin = new Thickness(16) };
        var add = new Button { Content = "Add device..." };
        add.Click += async (_, _) => await AddDeviceAsync();
        outer.Children.Add(add);

        _devicesPanel = new StackPanel { Spacing = 8 };
        outer.Children.Add(_devicesPanel);

        RefreshDevices();
        return new ScrollViewer { Content = outer };
    }

    private void RefreshDevices()
    {
        if (_devicesPanel is null) return;
        _devicesPanel.Children.Clear();

        var devices = C.Config.Devices;
        if (devices.Count == 0)
        {
            _devicesPanel.Children.Add(new TextBlock { Text = "No devices yet. Click \"Add device...\"." });
            return;
        }

        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            var index = i;

            var card = new StackPanel { Spacing = 6 };
            card.Children.Add(new TextBlock
            {
                Text = $"{device.Name}   ({device.Address})",
                FontWeight = FontWeights.SemiBold,
            });
            card.Children.Add(new TextBlock { Text = "Status: " + C.StatusFor(device).Label });

            var enabled = new ToggleSwitch { Header = "Enabled", IsOn = device.Enabled };
            enabled.Toggled += (_, _) => C.Mutate(_ => device.Enabled = enabled.IsOn, reevaluate: true);

            var pairing = new CheckBox { Content = "Manage pairing (pair on connect / unpair on disconnect)", IsChecked = device.ManagePairing };
            pairing.Click += (_, _) => C.Mutate(_ => device.ManagePairing = pairing.IsChecked == true);

            var delay = new NumberBox
            {
                Header = "Connect delay (ms)",
                Value = device.ConnectDelayMs,
                Minimum = 0,
                Maximum = 60000,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                SmallChange = 100,
            };
            delay.ValueChanged += (_, e) =>
            {
                if (!double.IsNaN(e.NewValue)) C.Mutate(_ => device.ConnectDelayMs = (int)e.NewValue);
            };

            var up = new Button { Content = "Up", IsEnabled = index > 0 };
            up.Click += (_, _) => MoveDevice(index, index - 1);
            var down = new Button { Content = "Down", IsEnabled = index < devices.Count - 1 };
            down.Click += (_, _) => MoveDevice(index, index + 1);
            var del = new Button { Content = "Delete" };
            del.Click += (_, _) => C.Mutate(cfg => cfg.Devices.RemoveAll(d => d.Id == device.Id), refreshMonitoring: true, reevaluate: true);
            var conn = new Button { Content = "Connect" };
            conn.Click += async (_, _) => await C.TestConnectAsync(device);
            var disc = new Button { Content = "Disconnect" };
            disc.Click += async (_, _) => await C.TestDisconnectAsync(device);

            card.Children.Add(enabled);
            card.Children.Add(pairing);
            card.Children.Add(delay);
            card.Children.Add(Row(up, down, del, conn, disc));

            _devicesPanel.Children.Add(Card(card));
        }
    }

    private void MoveDevice(int from, int to)
    {
        C.Mutate(cfg =>
        {
            var list = cfg.Devices;
            if (to < 0 || to >= list.Count) return;
            (list[from], list[to]) = (list[to], list[from]);
        }, refreshMonitoring: true);
    }

    private async Task AddDeviceAsync()
    {
        var paired = await C.PairedDevicesAsync();

        var combo = new ComboBox { Header = "Paired device", Width = 320 };
        foreach (var p in paired) combo.Items.Add($"{p.Name}  [{p.Address}]");
        var manualName = new TextBox { Header = "Name", PlaceholderText = "Device name" };
        var manualAddr = new TextBox { Header = "Address (MAC)", PlaceholderText = "3C:50:02:BF:22:45" };

        combo.SelectionChanged += (_, _) =>
        {
            var i = combo.SelectedIndex;
            if (i >= 0 && i < paired.Count)
            {
                manualName.Text = paired[i].Name;
                manualAddr.Text = paired[i].Address;
            }
        };

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "Pick a paired device, or type a name + MAC.", TextWrapping = TextWrapping.WrapWholeWords });
        body.Children.Add(combo);
        body.Children.Add(manualName);
        body.Children.Add(manualAddr);

        var dialog = new ContentDialog
        {
            Title = "Add device",
            Content = body,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(manualAddr.Text)) return;

        var device = new BTDevice
        {
            Name = string.IsNullOrWhiteSpace(manualName.Text) ? manualAddr.Text.Trim() : manualName.Text.Trim(),
            Address = manualAddr.Text.Trim(),
        };
        C.Mutate(cfg => cfg.Devices.Add(device), refreshMonitoring: true);
    }

    // ===================== General =====================

    private UIElement BuildGeneralTab()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(16) };

        panel.Children.Add(Header("Timing"));
        panel.Children.Add(NumberRow("Arrival debounce (ms)", C.Config.ArrivalDebounceMs, 0, 10000, v => C.Mutate(c => c.ArrivalDebounceMs = v)));
        panel.Children.Add(NumberRow("Departure debounce (ms)", C.Config.DebounceMs, 0, 10000, v => C.Mutate(c => c.DebounceMs = v)));
        panel.Children.Add(NumberRow("Connect retries", C.Config.ConnectRetryMax, 1, 20, v => C.Mutate(c => c.ConnectRetryMax = v)));
        panel.Children.Add(NumberRow("Retry base interval (s)", C.Config.ConnectRetrySecs, 1, 30, v => C.Mutate(c => c.ConnectRetrySecs = v)));
        panel.Children.Add(NumberRow("Per-call timeout (s)", C.Config.BtCallTimeoutSecs, 5, 120, v => C.Mutate(c => c.BtCallTimeoutSecs = v)));

        panel.Children.Add(Header("Behavior"));
        panel.Children.Add(ToggleRow("Pause automation", C.Config.Paused, on => C.Mutate(c => c.Paused = on, reevaluate: true)));
        panel.Children.Add(ToggleRow("Show notifications", C.Config.ShowNotifications, on => C.Mutate(c => c.ShowNotifications = on)));
        panel.Children.Add(ToggleRow("Notify on unexpected disconnect", C.Config.NotifyUnexpectedDisconnect, on => C.Mutate(c => c.NotifyUnexpectedDisconnect = on)));
        panel.Children.Add(ToggleRow("Launch at login", C.Config.LaunchAtLogin, on => C.SetLaunchAtLogin(on)));

        panel.Children.Add(Header("Global shortcuts"));
        panel.Children.Add(ToggleRow("Enable global shortcuts", C.Config.GlobalHotkeysEnabled, on => C.SetGlobalHotkeysEnabled(on)));
        panel.Children.Add(ShortcutRow("Pause", HotKeyAction.TogglePause, C.Config.HotkeyPause ?? HotKeyService.DefaultPause));
        panel.Children.Add(ShortcutRow("Connect all", HotKeyAction.ConnectAll, C.Config.HotkeyConnectAll ?? HotKeyService.DefaultConnectAll));
        panel.Children.Add(ShortcutRow("Disconnect all", HotKeyAction.DisconnectAll, C.Config.HotkeyDisconnectAll ?? HotKeyService.DefaultDisconnectAll));
        var restore = new Button { Content = "Restore default shortcuts" };
        restore.Click += (_, _) =>
        {
            C.RestoreDefaultHotkeys();
            UpdateShortcutLabel(HotKeyAction.TogglePause, "Pause", HotKeyService.DefaultPause);
            UpdateShortcutLabel(HotKeyAction.ConnectAll, "Connect all", HotKeyService.DefaultConnectAll);
            UpdateShortcutLabel(HotKeyAction.DisconnectAll, "Disconnect all", HotKeyService.DefaultDisconnectAll);
        };
        panel.Children.Add(restore);

        panel.Children.Add(Header("Configuration"));
        var export = new Button { Content = "Export..." };
        export.Click += async (_, _) => await ExportAsync();
        var import = new Button { Content = "Import..." };
        import.Click += async (_, _) => await ImportAsync();
        panel.Children.Add(Row(export, import));
        panel.Children.Add(new TextBlock { Text = "Config file: " + C.ConfigPath, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        return new ScrollViewer { Content = panel };
    }

    private async Task ExportAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = "autoswitchkvm-config" };
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        new ConfigStore(file.Path).Save(C.Config);   // reuse the store's serializer
    }

    private async Task ImportAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var imported = new ConfigStore(file.Path).Load();
        C.ReplaceConfig(imported);
        RefreshDevices();
        RefreshSource();
    }

    // ===================== Extras =====================

    private UIElement BuildExtrasTab()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(16) };
        panel.Children.Add(Header("Extras"));
        panel.Children.Add(new TextBlock
        {
            Text = "Nothing here yet on Windows. (Dock auto-hide is macOS-only.)",
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        return panel;
    }

    // ===================== Diagnostics =====================

    private UIElement BuildDiagnosticsTab()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(16) };

        panel.Children.Add(Header("Live state"));
        _diagText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        panel.Children.Add(_diagText);

        var refresh = new Button { Content = "Refresh USB snapshot" };
        refresh.Click += (_, _) => RefreshDiagnostics();
        panel.Children.Add(refresh);

        panel.Children.Add(Header("Debug log"));
        panel.Children.Add(new TextBlock { Text = "Log file: " + Log.FilePath, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        _logBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 240,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Text = Log.PlainText(),
        };
        panel.Children.Add(new ScrollViewer { Content = _logBox, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });

        var copy = new Button { Content = "Copy log" };
        copy.Click += (_, _) =>
        {
            var pkg = new DataPackage();
            pkg.SetText(Log.PlainText());
            Clipboard.SetContent(pkg);
        };
        var clear = new Button { Content = "Clear log" };
        clear.Click += (_, _) => Log.Clear();
        panel.Children.Add(Row(copy, clear));

        RefreshDiagnostics();
        return new ScrollViewer { Content = panel };
    }

    private void RefreshDiagnostics()
    {
        if (_diagText is null) return;
        var lines = new List<string>
        {
            $"Status:            {C.StatusText}",
            $"Bluetooth powered: {C.BluetoothPowered}",
            $"Selected:          {C.Selected}",
            "",
            "Devices:",
        };
        foreach (var d in C.Config.Devices)
            lines.Add($"  {d.Name,-20} {C.StatusFor(d).Label}");

        lines.Add("");
        lines.Add("Matching USB devices:");
        var source = C.Config.Source;
        foreach (var u in C.UsbSnapshot())
            if (source != null && u.VendorID == source.VendorID && source.ProductIDs.Contains(u.ProductID))
                lines.Add($"  0x{u.VendorID:X4}:0x{u.ProductID:X4}  {u.Name}");

        _diagText.Text = string.Join(Environment.NewLine, lines);
    }

    // ===================== Helpers =====================

    private static TextBlock Header(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }

    private static Border Card(UIElement content) => new()
    {
        BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12),
        Child = content,
    };

    private static StackPanel NumberRow(string label, int value, int min, int max, Action<int> onChange)
    {
        var box = new NumberBox
        {
            Header = label,
            Value = value,
            Minimum = min,
            Maximum = max,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 1,
            LargeChange = 10,
        };
        box.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) onChange((int)e.NewValue); };
        var wrap = new StackPanel();
        wrap.Children.Add(box);
        return wrap;
    }

    private static StackPanel ToggleRow(string label, bool isOn, Action<bool> onChange)
    {
        var toggle = new ToggleSwitch { Header = label, IsOn = isOn };
        toggle.Toggled += (_, _) => onChange(toggle.IsOn);
        var wrap = new StackPanel();
        wrap.Children.Add(toggle);
        return wrap;
    }

    private UIElement ShortcutRow(string label, HotKeyAction action, KeyShortcut current)
    {
        var text = new TextBlock
        {
            Text = $"{label}: {current.Display}",
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 220,
        };
        _shortcutLabels[action] = text;

        var record = new Button { Content = "Record..." };
        record.Click += async (_, _) => await RecordShortcutAsync(label, action);
        return Row(text, record);
    }

    private void UpdateShortcutLabel(HotKeyAction action, string label, KeyShortcut s)
    {
        if (_shortcutLabels.TryGetValue(action, out var tb)) tb.Text = $"{label}: {s.Display}";
    }

    private async Task RecordShortcutAsync(string label, HotKeyAction action)
    {
        var preview = new TextBlock { Text = "...", FontWeight = FontWeights.SemiBold };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = "Press the shortcut (must include Ctrl / Alt / Shift / Win).",
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        body.Children.Add(preview);

        var dialog = new ContentDialog
        {
            Title = $"Record: {label}",
            Content = body,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = Root.XamlRoot,
            IsPrimaryButtonEnabled = false,
        };

        KeyShortcut? captured = null;
        dialog.KeyDown += (_, e) =>
        {
            if (IsModifierKey(e.Key)) return;          // wait for the non-modifier key
            var mods = CurrentModifiers(out var disp);
            if (mods == 0) return;                      // require at least one modifier
            captured = new KeyShortcut { KeyCode = (uint)e.Key, Modifiers = mods, Display = disp + KeyName(e.Key) };
            preview.Text = captured.Display;
            dialog.IsPrimaryButtonEnabled = true;
            e.Handled = true;
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && captured != null)
        {
            C.SetHotkey(action, captured);
            UpdateShortcutLabel(action, label, captured);
        }
    }

    private static bool IsModifierKey(VirtualKey k) =>
        k is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
          or VirtualKey.LeftControl or VirtualKey.RightControl
          or VirtualKey.LeftShift or VirtualKey.RightShift
          or VirtualKey.LeftMenu or VirtualKey.RightMenu
          or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static uint CurrentModifiers(out string display)
    {
        uint mods = 0;
        var sb = "";
        static bool Down(VirtualKey k) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

        if (Down(VirtualKey.Control)) { mods |= HotKeyService.MOD_CONTROL; sb += "Ctrl+"; }
        if (Down(VirtualKey.Menu)) { mods |= HotKeyService.MOD_ALT; sb += "Alt+"; }
        if (Down(VirtualKey.Shift)) { mods |= HotKeyService.MOD_SHIFT; sb += "Shift+"; }
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) { mods |= HotKeyService.MOD_WIN; sb += "Win+"; }

        display = sb;
        return mods;
    }

    private static string KeyName(VirtualKey k)
    {
        if (k >= VirtualKey.A && k <= VirtualKey.Z) return ((char)('A' + (k - VirtualKey.A))).ToString();
        if (k >= VirtualKey.Number0 && k <= VirtualKey.Number9) return ((char)('0' + (k - VirtualKey.Number0))).ToString();
        return k.ToString();
    }

    private static bool TryParseHex(string? text, out ushort value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
        return ushort.TryParse(t, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
