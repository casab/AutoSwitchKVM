using AutoSwitchKVM.App.Platform;
using AutoSwitchKVM.App.Support;
using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using Microsoft.UI.Dispatching;

namespace AutoSwitchKVM.App.Services;

/// Coordinator: owns the config, engine, USB monitor and Bluetooth controller, wires them together,
/// and exposes state + actions to the UI (tray menu and Settings). Mirrors the macOS AppController.
///
/// Lives on the UI thread (constructed from App.OnLaunched). The monitors marshal their callbacks to
/// the captured SynchronizationContext (the UI thread), and the status poll is marshaled via the
/// DispatcherQueue, so the single-threaded SelectionEngine is only ever touched on the UI thread.
public sealed class AppController
{
    private readonly ConfigStore _store = new();
    private readonly PnpUsbMonitor _usb;
    private readonly WinRtBluetooth _bt;
    private readonly PowerMonitor _power = new();
    private readonly HotKeyService _hotkeys = new();
    private readonly ToastNotifier _toasts = new();
    private readonly DispatcherQueue _dispatcher;
    private Timer? _pollTimer;

    public AppConfig Config { get; private set; }
    public SelectionEngine Engine { get; }

    /// Raised (on the UI thread) whenever state the UI shows may have changed.
    public event Action? StateChanged;

    public AppController()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _usb = new PnpUsbMonitor(_dispatcher);   // marshal callbacks onto the UI thread
        _bt = new WinRtBluetooth(_dispatcher);
        Config = _store.Load();
        Log.Info("app", $"config loaded from {_store.Path}: profile='{Config.ActiveProfileName}', " +
            $"source={(Config.Source is null ? "(none)" : Config.Source.DisplayVidPid)}, devices={Config.Devices.Count}, paused={Config.Paused}");

        Engine = new SelectionEngine(Config, _usb, _bt);
        Engine.Changed += () => StateChanged?.Invoke();
        Engine.OnNotice = (title, body) =>
        {
            Log.Info("notice", $"{title}: {body}");
            if (Config.ShowNotifications) _toasts.Notify(title, body);
        };
        Engine.OnUnexpectedDisconnect = d =>
        {
            Log.Warn("bt", $"{d.Name} dropped unexpectedly while selected");
            if (Config.NotifyUnexpectedDisconnect)
                _toasts.Notify("Device disconnected", $"{d.Name} dropped while still selected");
        };
    }

    public void Start()
    {
        Log.Info("app", $"starting; log file: {Log.FilePath}");
        _toasts.Register();

        _usb.Changed += OnUsbSnapshot;
        _usb.Start();
        Log.Info("usb", "monitor started");
        Engine.RefreshMonitoring();
        _ = Engine.SeedAsync();
        _pollTimer = new Timer(_ => _dispatcher.TryEnqueue(() => { _ = Engine.PollStatusesAsync(); }),
            null, 10_000, 10_000);

        // Sleep/wake: release on suspend, reconcile on resume (marshaled onto the UI thread).
        _power.OnSuspend = () => _dispatcher.TryEnqueue(() =>
        {
            Log.Info("power", "suspend -> disconnect all");
            _ = Engine.DisconnectAllNowAsync();
        });
        _power.OnResume = () => _dispatcher.TryEnqueue(() =>
        {
            Log.Info("power", "resume -> re-evaluate");
            _ = Engine.ReevaluateAsync();
        });
        _power.Start();

        // Global hotkeys (WM_HOTKEY fires on this UI thread).
        _hotkeys.OnAction = a => _dispatcher.TryEnqueue(() => HandleHotkey(a));
        _hotkeys.Start();
        ApplyHotkeys();
        Log.Info("hotkey", $"global hotkeys {(Config.GlobalHotkeysEnabled ? "enabled" : "disabled")}");

        LoginItem.SetEnabled(Config.LaunchAtLogin);

        Log.Info("app", "started");
    }

    public void Shutdown()
    {
        _pollTimer?.Dispose();
        _hotkeys.Dispose();
        _power.Dispose();
        _toasts.Unregister();
        _usb.Stop();
        _bt.StopMonitoring();
        Save();
    }

    // ---- System integration ----

    private void HandleHotkey(HotKeyAction action)
    {
        switch (action)
        {
            case HotKeyAction.TogglePause: TogglePause(); break;
            case HotKeyAction.ConnectAll: _ = ConnectAllAsync(); break;
            case HotKeyAction.DisconnectAll: _ = DisconnectAllAsync(); break;
        }
    }

    public void ApplyHotkeys() =>
        _hotkeys.Apply(Config.GlobalHotkeysEnabled, Config.HotkeyPause, Config.HotkeyConnectAll, Config.HotkeyDisconnectAll);

    public void SetGlobalHotkeysEnabled(bool enabled)
    {
        Config.GlobalHotkeysEnabled = enabled;
        Save();
        ApplyHotkeys();
        StateChanged?.Invoke();
    }

    public void SetHotkey(HotKeyAction action, KeyShortcut shortcut)
    {
        switch (action)
        {
            case HotKeyAction.TogglePause: Config.HotkeyPause = shortcut; break;
            case HotKeyAction.ConnectAll: Config.HotkeyConnectAll = shortcut; break;
            case HotKeyAction.DisconnectAll: Config.HotkeyDisconnectAll = shortcut; break;
        }
        Save();
        ApplyHotkeys();
        StateChanged?.Invoke();
    }

    public void RestoreDefaultHotkeys()
    {
        Config.HotkeyPause = HotKeyService.DefaultPause;
        Config.HotkeyConnectAll = HotKeyService.DefaultConnectAll;
        Config.HotkeyDisconnectAll = HotKeyService.DefaultDisconnectAll;
        Save();
        ApplyHotkeys();
        StateChanged?.Invoke();
    }

    public void SetLaunchAtLogin(bool enabled)
    {
        Config.LaunchAtLogin = enabled;
        Save();
        LoginItem.SetEnabled(enabled);
        StateChanged?.Invoke();
    }

    private void OnUsbSnapshot(IReadOnlyList<UsbDeviceInfo> snapshot) => Engine.OnUsbSnapshot(snapshot);

    // ---- State for the UI ----

    public bool Paused => Config.Paused;
    public bool Selected => Engine.Selected;
    public bool BluetoothPowered => Engine.BluetoothPowered;

    public string StatusText =>
        Config.Paused ? "Paused"
        : !Engine.BluetoothPowered ? "Bluetooth off"
        : Engine.Selected ? "This PC is selected"
        : "Switched away";

    public DeviceStatus StatusFor(BTDevice device) =>
        Engine.Statuses.TryGetValue(device.Id, out var s) ? s : DeviceStatus.Idle;

    // ---- Quick actions ----

    public Task ConnectAllAsync() { Log.Info("action", "connect all"); return Engine.ConnectAllNowAsync(); }
    public Task DisconnectAllAsync() { Log.Info("action", "disconnect all"); return Engine.DisconnectAllNowAsync(); }

    public void TogglePause()
    {
        Config.Paused = !Config.Paused;
        Log.Info("action", $"pause -> {Config.Paused}");
        Save();
        _ = Engine.EvaluateNowAsync();
        StateChanged?.Invoke();
    }

    public void SwitchProfile(Guid id)
    {
        Config.ActiveProfileID = id;
        Log.Info("action", $"switch profile -> '{Config.ActiveProfileName}'");
        Save();
        Engine.RefreshMonitoring();
        _ = Engine.ReevaluateAsync();
        StateChanged?.Invoke();
    }

    // ---- Per-device test actions (Settings buttons) ----

    // Manual test actions catch failures and log them - they run from button-click (async void)
    // handlers, so an uncaught BluetoothException would otherwise crash the app.

    public async Task TestConnectAsync(BTDevice d)
    {
        Log.Info("action", $"connect {d.Name}");
        try { await _bt.ConnectAsync(d.Address); }
        catch (Exception ex) { Log.Error("bt", $"connect {d.Name} failed", ex); }
    }

    /// Release a device from this PC: disconnect, and (for Classic-HID / managePairing devices, where
    /// the bond is the only lever) unpair. Mirrors the engine's switch-away path. A plain
    /// DisconnectAsync is a no-op on Windows, so this is what the tray/Settings "disconnect" must call.
    public async Task TestDisconnectAsync(BTDevice d)
    {
        Log.Info("action", $"release {d.Name} (managePairing={d.ManagePairing})");
        try
        {
            await _bt.DisconnectAsync(d.Address);
            if (d.ManagePairing) await _bt.UnpairAsync(d.Address);
        }
        catch (Exception ex) { Log.Error("bt", $"release {d.Name} failed", ex); }
    }

    public async Task TestPairAsync(BTDevice d)
    {
        Log.Info("action", $"pair {d.Name}");
        try { await _bt.PairAsync(d.Address); }
        catch (Exception ex) { Log.Error("bt", $"pair {d.Name} failed", ex); }
    }

    public async Task TestUnpairAsync(BTDevice d)
    {
        Log.Info("action", $"unpair {d.Name}");
        try { await _bt.UnpairAsync(d.Address); }
        catch (Exception ex) { Log.Error("bt", $"unpair {d.Name} failed", ex); }
    }

    /// Dump a full Bluetooth diagnostic for this device to the log (radio, FromBluetoothAddress state,
    /// paired/unpaired/all-Bluetooth device lists with the target marked, HID nodes).
    public async Task DiagnoseAsync(BTDevice d)
    {
        Log.Info("action", $"diagnose {d.Name} ({d.Address})");
        try { await _bt.DiagnoseAsync(d.Address); }
        catch (Exception ex) { Log.Error("diag", "diagnose failed", ex); }
    }

    public Task<IReadOnlyList<PairedDeviceInfo>> PairedDevicesAsync() => _bt.PairedDevicesAsync();
    public IReadOnlyList<UsbDeviceInfo> UsbSnapshot() => _usb.Snapshot();

    public SourceLearner CreateLearner() => new(_usb);

    // ---- Config mutation ----

    /// Apply a change to the active config, persist it, and refresh as needed.
    public void Mutate(Action<AppConfig> change, bool refreshMonitoring = false, bool reevaluate = false)
    {
        change(Config);
        Save();
        if (refreshMonitoring) Engine.RefreshMonitoring();
        if (reevaluate) _ = Engine.ReevaluateAsync();
        StateChanged?.Invoke();
    }

    /// Replace the whole config (e.g. after an import) and re-seed everything.
    public void ReplaceConfig(AppConfig config)
    {
        Config = config.Normalized();
        Engine.Config = Config;
        Save();
        Engine.RefreshMonitoring();
        _ = Engine.ReevaluateAsync();
        StateChanged?.Invoke();
    }

    public void Save() => _store.Save(Config);
    public string ConfigPath => _store.Path;
}
