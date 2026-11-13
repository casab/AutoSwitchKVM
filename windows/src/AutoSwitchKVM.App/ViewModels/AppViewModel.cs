using AutoSwitchKVM.App.Support;
using System.Collections.ObjectModel;
using AutoSwitchKVM.App.Platform;
using AutoSwitchKVM.App.Services;
using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace AutoSwitchKVM.App.ViewModels;

public enum HostState { Selected, Away, Paused, BluetoothOff, NoSource }

public sealed class ProfileItem
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public override string ToString() => Name;
}

/// The single view-model behind the Settings window and the tray panel. Reads live state from the
/// AppController and writes settings back through AppController.Mutate (which persists them).
///
/// Refresh model: AppController.StateChanged fires on every engine/config change; Refresh() then
/// syncs the collections in place and raises a blanket PropertyChanged so every x:Bind re-reads.
/// Config-backed properties read the config directly and only call Mutate when the value really
/// changes, so the two-way echo from a refresh never writes anything.
public sealed class AppViewModel : ObservableObject
{
    public static AppViewModel Instance { get; private set; } = null!;

    public static void Initialize(AppController controller) => Instance = new AppViewModel(controller);

    private readonly AppController _c;
    private readonly DispatcherQueue _dispatcher;
    private string? _profilesSignature;

    private AppViewModel(AppController controller)
    {
        _c = controller;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _c.StateChanged += () => _dispatcher.Enqueue("settings refresh", Refresh);
        Refresh();
    }

    public AppController Controller => _c;
    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<ProfileItem> Profiles { get; } = new();

    // ---------------- Status ----------------

    public HostState State =>
        _c.Paused ? HostState.Paused
        : !_c.BluetoothPowered ? HostState.BluetoothOff
        : _c.Config.Source is null ? HostState.NoSource
        : _c.Selected ? HostState.Selected
        : HostState.Away;

    public string StatusTitle => State switch
    {
        HostState.Paused => "Automation paused",
        HostState.BluetoothOff => "Bluetooth is off",
        HostState.NoSource => "Set up your KVM",
        HostState.Selected => "This PC is selected",
        _ => "Switched to the other computer",
    };

    public string StatusDetail
    {
        get
        {
            if (State == HostState.NoSource) return "Choose the USB device that tells AutoSwitch which computer the KVM is on.";
            if (State == HostState.BluetoothOff) return "Turn Bluetooth on to hand devices over.";
            var devices = _c.Config.Devices.Where(d => d.Enabled).ToList();
            if (devices.Count == 0) return "Add a Bluetooth device to hand it over automatically.";
            var connected = devices.Count(d => _c.StatusFor(d).Kind == DeviceStatusKind.Connected);
            var connecting = devices.Any(d => _c.StatusFor(d).Kind == DeviceStatusKind.Connecting);
            var prefix = State == HostState.Paused ? "Switching is ignored until you resume. " : "";
            if (connecting) return prefix + "Connecting…";
            if (devices.Count == 1)
                return prefix + $"{devices[0].Name} — {(connected == 1 ? "connected" : "not connected")}";
            return prefix + $"{connected} of {devices.Count} devices connected";
        }
    }

    public string StatusGlyph => State switch
    {
        HostState.Paused => "",        // Pause
        HostState.BluetoothOff => "",  // Warning
        HostState.NoSource => "",      // USB
        HostState.Selected => "",      // Monitor
        _ => "",                        // Switch
    };

    public Brush StatusForeground => Theme.Brush(State switch
    {
        HostState.Selected => "SystemFillColorSuccessBrush",
        HostState.Paused => "SystemFillColorCautionBrush",
        HostState.BluetoothOff => "SystemFillColorCriticalBrush",
        HostState.NoSource => "AccentFillColorDefaultBrush",
        _ => "TextFillColorSecondaryBrush",
    });

    public Brush StatusBackground => Theme.Brush(State switch
    {
        HostState.Selected => "SystemFillColorSuccessBackgroundBrush",
        HostState.Paused => "SystemFillColorCautionBackgroundBrush",
        HostState.BluetoothOff => "SystemFillColorCriticalBackgroundBrush",
        HostState.NoSource => "SystemFillColorAttentionBackgroundBrush",
        _ => "SystemFillColorNeutralBackgroundBrush",
    });

    // ---------------- Source ----------------

    public bool HasSource => _c.Config.Source is not null;
    public bool NoSource => _c.Config.Source is null;
    public string SourceName => _c.Config.Source?.Name ?? "No source";
    public string SourceIds => _c.Config.Source?.DisplayVidPid ?? "Not set";
    public string SourcePresence =>
        _c.Config.Source is null ? "" : _c.Selected ? "Connected to this PC" : "Not connected to this PC";
    public Brush SourcePresenceBrush => Theme.Brush(_c.Selected ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");

    // ---------------- Devices ----------------

    public bool HasDevices => Devices.Count > 0;
    public bool NoDevices => Devices.Count == 0;

    // ---------------- Profiles ----------------

    public bool HasMultipleProfiles => Profiles.Count > 1;
    public string ActiveProfileName => _c.Config.ActiveProfileName;

    public ProfileItem? SelectedProfile
    {
        get => Profiles.FirstOrDefault(p => p.Id == _c.Config.ActiveProfileID);
        set
        {
            if (value is null || value.Id == _c.Config.ActiveProfileID) return;
            _c.SwitchProfile(value.Id);
        }
    }

    // ---------------- Behavior ----------------

    public bool IsPaused
    {
        get => _c.Paused;
        set { if (value != _c.Paused) _c.TogglePause(); }
    }

    public bool ShowNotifications
    {
        get => _c.Config.ShowNotifications;
        set { if (value != _c.Config.ShowNotifications) _c.Mutate(c => c.ShowNotifications = value); }
    }

    public bool NotifyUnexpectedDisconnect
    {
        get => _c.Config.NotifyUnexpectedDisconnect;
        set { if (value != _c.Config.NotifyUnexpectedDisconnect) _c.Mutate(c => c.NotifyUnexpectedDisconnect = value); }
    }

    public bool AutoReconnectOnDrop
    {
        get => _c.Config.AutoReconnectOnDrop;
        set { if (value != _c.Config.AutoReconnectOnDrop) _c.Mutate(c => c.AutoReconnectOnDrop = value); }
    }

    public bool LaunchAtLogin
    {
        get => _c.Config.LaunchAtLogin;
        set { if (value != _c.Config.LaunchAtLogin) _c.SetLaunchAtLogin(value); }
    }

    // ---------------- Timing (NumberBox binds doubles) ----------------

    public double ArrivalDebounceMs { get => _c.Config.ArrivalDebounceMs; set => SetInt(value, _c.Config.ArrivalDebounceMs, (c, v) => c.ArrivalDebounceMs = v); }
    public double DebounceMs { get => _c.Config.DebounceMs; set => SetInt(value, _c.Config.DebounceMs, (c, v) => c.DebounceMs = v); }
    public double ConnectRetryMax { get => _c.Config.ConnectRetryMax; set => SetInt(value, _c.Config.ConnectRetryMax, (c, v) => c.ConnectRetryMax = v); }
    public double ConnectRetrySecs { get => _c.Config.ConnectRetrySecs; set => SetInt(value, _c.Config.ConnectRetrySecs, (c, v) => c.ConnectRetrySecs = v); }
    public double BtCallTimeoutSecs { get => _c.Config.BtCallTimeoutSecs; set => SetInt(value, _c.Config.BtCallTimeoutSecs, (c, v) => c.BtCallTimeoutSecs = v); }
    public double PostUnpairSettleMs { get => _c.Config.PostUnpairSettleMs; set => SetInt(value, _c.Config.PostUnpairSettleMs, (c, v) => c.PostUnpairSettleMs = v); }

    private void SetInt(double value, int current, Action<AppConfig, int> apply)
    {
        if (double.IsNaN(value)) return;
        var v = (int)Math.Round(value);
        if (v != current) _c.Mutate(c => apply(c, v));
    }

    // ---------------- Shortcuts ----------------

    public bool GlobalHotkeysEnabled
    {
        get => _c.Config.GlobalHotkeysEnabled;
        set { if (value != _c.Config.GlobalHotkeysEnabled) _c.SetGlobalHotkeysEnabled(value); }
    }

    public string PauseShortcut => _c.Config.HotkeyPause?.Display ?? "Not set";
    public string ConnectAllShortcut => _c.Config.HotkeyConnectAll?.Display ?? "Not set";
    public string DisconnectAllShortcut => _c.Config.HotkeyDisconnectAll?.Display ?? "Not set";

    public void SetShortcut(HotKeyAction action, KeyShortcut? shortcut) => _c.SetHotkey(action, shortcut);
    public void RestoreDefaultShortcuts() => _c.RestoreDefaultHotkeys();

    // ---------------- Diagnostics ----------------

    public string BluetoothText => _c.BluetoothPowered ? "On" : "Off";
    public string SelectedText => _c.Selected ? "Yes — the KVM source is connected" : "No";
    public string ConfigPath => _c.ConfigPath;

    /// Settings ▸ Diagnostics ▸ Log detail, as a ComboBox index:
    /// 0 = errors only, 1 = warnings, 2 = normal (info, default), 3 = detailed (debug).
    public int LogLevelIndex
    {
        get => LogLevels.Parse(_c.Config.LogLevel) switch
        {
            Core.LogLevel.Error => 0,
            Core.LogLevel.Warn => 1,
            Core.LogLevel.Debug => 3,
            _ => 2,
        };
        set
        {
            if (value < 0 || value == LogLevelIndex) return;
            _c.SetLogLevel(value switch
            {
                0 => Core.LogLevel.Error,
                1 => Core.LogLevel.Warn,
                3 => Core.LogLevel.Debug,
                _ => Core.LogLevel.Info,
            });
        }
    }

    /// From the monitor's cached snapshot (no WMI call on the UI thread).
    public string MatchingUsbText => DescribeMatchingUsb(_c.Config.Source, _c.LastUsbSnapshot);

    public static string DescribeMatchingUsb(USBSource? source, IReadOnlyList<UsbDeviceInfo> snapshot)
    {
        if (source is null) return "No KVM source set up.";
        var lines = snapshot
            .Where(u => u.VendorID == source.VendorID && source.ProductIDs.Contains(u.ProductID))
            .Select(u => $"{u.VendorID:X4}:{u.ProductID:X4}  {u.Name}")
            .Distinct()
            .ToList();
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "None connected right now.";
    }
    public string LogPath => Support.Log.FilePath;

    // ---------------- About & updates ----------------

    private static UpdateService? Updates => App.Updates;

    public string VersionText => $"Version {Updates?.CurrentVersion ?? typeof(AppViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev"}";

    public string UpdateStatusText => Updates?.State switch
    {
        UpdateState.NotInstalled => "Development build: automatic updates are off.",
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => "You're up to date.",
        UpdateState.Downloading => $"Downloading {Updates.AvailableVersion}… {Updates.DownloadPercent}%",
        UpdateState.ReadyToRestart => $"Version {Updates.AvailableVersion} is ready. Restart to update.",
        UpdateState.Failed => $"Couldn't check for updates: {Updates.LastError}",
        UpdateState.Idle => "Updates are checked automatically.",
        _ => "",
    };

    public string UpdateButtonText => Updates?.State == UpdateState.ReadyToRestart ? "Restart to update" : "Check for updates";

    public bool UpdateButtonEnabled => Updates is { IsInstalled: true } u
        && u.State is not (UpdateState.Checking or UpdateState.Downloading);

    public void UpdateAction()
    {
        if (Updates is null) return;
        if (Updates.State == UpdateState.ReadyToRestart) (Microsoft.UI.Xaml.Application.Current as App)?.RestartForUpdate();
        else _ = Updates.CheckAsync(userInitiated: true);
    }

    // ---------------- Actions ----------------

    public void ConnectAll() => _c.ConnectAll();
    public void DisconnectAll() => _c.DisconnectAll();
    public void TogglePause() => _c.TogglePause();
    public void AddProfile() => _c.AddProfile();
    public void RenameProfile(string name) => _c.RenameActiveProfile(name);
    public void DeleteProfile() => _c.DeleteActiveProfile();
    public bool CanDeleteProfile => _c.Config.Profiles.Count > 1;

    public void MoveDevice(Guid id, int delta) => _c.Mutate(cfg =>
    {
        var list = cfg.Devices;
        var from = list.FindIndex(d => d.Id == id);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= list.Count) return;
        (list[from], list[to]) = (list[to], list[from]);
    }, refreshMonitoring: true);

    // Adding or removing a device touches only that device; the others keep their connections.
    public void RemoveDevice(Guid id)
    {
        _c.DeviceRemoved(id);
        _c.Mutate(cfg => cfg.Devices.RemoveAll(d => d.Id == id), refreshMonitoring: true);
    }

    public void AddDevice(BTDevice device)
    {
        _c.Mutate(cfg => cfg.Devices.Add(device), refreshMonitoring: true);
        _c.DeviceAddedOrEnabled(device.Id);
    }

    public void SetSource(USBSource? source) =>
        _c.Mutate(cfg => cfg.Source = source, refreshMonitoring: true, reevaluate: true);

    // ---------------- Refresh ----------------

    public void Refresh()
    {
        SyncProfiles();
        SyncDevices();
        OnPropertyChanged(string.Empty);   // x:Bind re-reads every binding on this object
    }

    private void SyncProfiles()
    {
        var signature = string.Join("|", _c.Config.Profiles.Select(p => $"{p.Id}:{p.Name}"));
        if (signature == _profilesSignature) return;
        _profilesSignature = signature;
        Profiles.Clear();
        foreach (var p in _c.Config.Profiles) Profiles.Add(new ProfileItem { Id = p.Id, Name = p.Name });
    }

    /// Bring Devices in line with the config with item-level changes (remove / insert / move),
    /// never Clear(): a reset makes the ItemsRepeaters recycle every row, and a row's expander
    /// state then showed up on another device (remove the 3rd device -> the 2nd one opened).
    /// The rest of a row's state lives in its DeviceViewModel (IsExpanded) for the same reason.
    private void SyncDevices()
    {
        var models = _c.Config.Devices;
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            var id = Devices[i].Id;
            if (!models.Any(m => m.Id == id)) Devices.RemoveAt(i);
        }
        for (var i = 0; i < models.Count; i++)
        {
            var model = models[i];
            var at = -1;
            for (var j = i; j < Devices.Count; j++)
                if (Devices[j].Id == model.Id) { at = j; break; }
            if (at < 0) Devices.Insert(i, new DeviceViewModel(this, model));
            else if (at != i) Devices.Move(at, i);
            Devices[i].Update(model, _c.StatusFor(model), i, models.Count);
        }
    }
}

/// Theme brush lookup (resolved for the current app theme when called; Refresh re-reads them).
internal static class Theme
{
    public static Brush Brush(string key) =>
        Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var value) && value is Brush b
            ? b
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
