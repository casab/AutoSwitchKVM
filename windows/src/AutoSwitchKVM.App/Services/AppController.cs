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
        _store.OnSaveError = ex => Log.Error("config", "config save failed", ex);
        Config = _store.Load();
        Log.Info("app", $"config loaded from {_store.Path}: profile='{Config.ActiveProfileName}', " +
            $"source={(Config.Source is null ? "(none)" : Config.Source.DisplayVidPid)}, devices={Config.Devices.Count}, paused={Config.Paused}");

        Engine = new SelectionEngine(Config, _usb, _bt, new AppLogSink());
        Engine.Changed += () => StateChanged?.Invoke();
        Engine.OnNotice = (title, body) =>
        {
            Log.Info("notice", $"{title}: {body}");
            if (Config.ShowNotifications) _toasts.Notify(title, body);
        };
        Engine.OnUnexpectedDisconnect = d =>
        {
            // The engine logs the drop itself; this only raises the optional toast.
            if (Config.NotifyUnexpectedDisconnect)
                _toasts.Notify("Device disconnected", $"{d.Name} dropped while still selected");
        };
    }

    /// Fire-and-forget with logging: a background call must never fault unobserved (it would
    /// only surface, if ever, when the GC finalizes the task — i.e. the error is effectively lost).
    private static void Fire(Func<Task> work, string what) => _ = RunLoggedAsync(work, what);

    private static async Task RunLoggedAsync(Func<Task> work, string what)
    {
        try { await work(); }
        catch (Exception ex) { Log.Error("app", $"{what} failed unexpectedly", ex); }
    }

    public void Start()
    {
        Log.Info("app", $"starting; log file: {Log.FilePath}");
        _toasts.Register();

        _usb.Changed += OnUsbSnapshot;
        _usb.Start();
        Log.Info("usb", "monitor started");
        Engine.RefreshMonitoring();
        Fire(Engine.SeedAsync, "initial seed");
        _pollTimer = new Timer(_ => _dispatcher.TryEnqueue(() => Fire(Engine.PollStatusesAsync, "status poll")),
            null, 10_000, 10_000);

        // Sleep/wake. Suspend runs on the SystemEvents thread and blocks it (bounded): an
        // enqueued-to-UI handler may never get pumped before the machine sleeps, and the unpair
        // is what frees the devices for the other host. NotifySuspending (thread-safe) first halts
        // the engine so it can't re-pair (e.g. an auto-reconnect after the "drop" our own release
        // causes). The release talks to the Bluetooth controller directly (the engine is
        // UI-thread-only); resume re-enables and re-seeds the engine so statuses reconcile.
        _power.OnSuspend = () =>
        {
            Engine.NotifySuspending();
            Log.Info("power", "suspend -> releasing enabled devices (bounded to 1.5 s)");
            try
            {
                // Task.Run keeps continuations off this thread's SynchronizationContext so the
                // bounded Wait cannot deadlock.
                var released = Task.Run(ReleaseEnabledDevicesAsync).Wait(TimeSpan.FromMilliseconds(1500));
                Log.Info("power", released ? "suspend release complete" : "suspend release still running at sleep");
            }
            catch (Exception ex)
            {
                Log.Warn("power", $"suspend release failed: {ex.Message}");
            }
        };
        _power.OnResume = () => _dispatcher.TryEnqueue(() =>
        {
            Log.Info("power", "resume -> re-evaluate");
            Engine.NotifyResumed();
            Fire(Engine.ReevaluateAsync, "resume re-evaluate");
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
        _store.Save(Config);
    }

    // ---- System integration ----

    /// Release every *enabled* device (disconnect + unpair where managed) — the pre-sleep path.
    /// Mirrors the macOS manualDisconnectAll: talks to the Bluetooth controller directly so it
    /// can run off the UI thread; the engine reconciles statuses on resume.
    private async Task ReleaseEnabledDevicesAsync()
    {
        var devices = Config.Devices.Where(d => d.Enabled).ToList();
        foreach (var device in devices)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _bt.DisconnectAsync(device.Address);
                if (device.ManagePairing) await _bt.UnpairAsync(device.Address);
                Log.Info("power", $"released {device.Name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                var kind = ex is BluetoothException b ? b.Kind.ToString() : ex.GetType().Name;
                Log.Warn("power", $"release {device.Name} failed after {sw.ElapsedMilliseconds} ms: [{kind}] {ex.Message}");
            }
        }
    }

    private void HandleHotkey(HotKeyAction action)
    {
        Log.Info("hotkey", $"{action}");
        switch (action)
        {
            case HotKeyAction.TogglePause: TogglePause(); break;
            case HotKeyAction.ConnectAll: ConnectAll(); break;
            case HotKeyAction.DisconnectAll: DisconnectAll(); break;
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

    public void SetHotkey(HotKeyAction action, KeyShortcut? shortcut)
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

    /// Tray icon file for the current state (mirrors the macOS menu-bar symbol set + StatusText
    /// precedence): paused -> Bluetooth off -> this-PC-selected -> switched away.
    public string TrayIconAsset =>
        Config.Paused ? "tray-paused.ico"
        : !Engine.BluetoothPowered ? "tray-off.ico"
        : Engine.Selected ? "tray.ico"
        : "tray-inactive.ico";

    // ---- Quick actions ----

    // Quick actions are fire-and-forget (tray menu / hotkeys); failures are logged by the engine
    // and, for anything unexpected, by Fire.
    public void ConnectAll() => Fire(Engine.ConnectAllNowAsync, "connect all");
    public void DisconnectAll() => Fire(Engine.DisconnectAllNowAsync, "disconnect all");

    public void TogglePause()
    {
        Config.Paused = !Config.Paused;
        Log.Info("action", $"pause -> {Config.Paused}");
        Save();
        // Resuming re-seeds so the engine reconciles to the current state (spec §3); pausing
        // just stops acting — presence keeps updating via the USB monitor.
        if (!Config.Paused) Fire(Engine.ReevaluateAsync, "re-evaluate after unpause");
        StateChanged?.Invoke();
    }

    public void SwitchProfile(Guid id)
    {
        if (Config.ActiveProfileID == id || Config.Profiles.All(p => p.Id != id)) return;
        Config.ActiveProfileID = id;
        Log.Info("action", $"switch profile -> '{Config.ActiveProfileName}'");
        Save();
        Engine.RefreshMonitoring();
        Fire(Engine.ReevaluateAsync, "re-evaluate after profile switch");
        StateChanged?.Invoke();
    }

    public void AddProfile()
    {
        var profile = new Profile { Name = UniqueProfileName("Profile") };
        Config.Profiles.Add(profile);
        Config.ActiveProfileID = profile.Id;
        Log.Info("action", $"add profile -> '{profile.Name}'");
        Save();
        Engine.RefreshMonitoring();
        Fire(Engine.ReevaluateAsync, "re-evaluate after add profile");
        StateChanged?.Invoke();
    }

    public void RenameActiveProfile(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        Config.ActiveProfileName = trimmed;
        Log.Info("action", $"rename profile -> '{trimmed}'");
        Save();
        StateChanged?.Invoke();
    }

    public void DeleteActiveProfile()
    {
        if (Config.Profiles.Count <= 1) return;
        var removed = Config.ActiveProfileID;
        Config.Profiles.RemoveAll(p => p.Id == removed);
        Config.ActiveProfileID = Config.Profiles[0].Id;
        Log.Info("action", $"delete profile -> now '{Config.ActiveProfileName}'");
        Save();
        Engine.RefreshMonitoring();
        Fire(Engine.ReevaluateAsync, "re-evaluate after delete profile");
        StateChanged?.Invoke();
    }

    private string UniqueProfileName(string baseName)
    {
        var names = Config.Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        var n = 2;
        while (names.Contains($"{baseName} {n}")) n++;
        return $"{baseName} {n}";
    }

    // ---- Per-device actions (tray device rows + Settings buttons) ----

    // These go through the engine so they share its run tracking, busy guard and status: a
    // manual action supersedes engine work on the same device instead of racing it, and its
    // result shows in the device status. They never throw (the callers are async-void click
    // handlers and tray commands, where an exception would crash the app or vanish).

    /// Full connect flow for one device (pair + connect for managePairing devices, with retries).
    public Task ConnectDeviceAsync(BTDevice d) => RunLoggedAsync(() => Engine.ConnectDeviceNowAsync(d), $"connect {d.Name}");

    /// Release a device from this PC: disconnect, and (for Classic-HID / managePairing devices, where
    /// the bond is the only lever) unpair. A plain DisconnectAsync is a no-op on Windows, so this is
    /// what the tray/Settings "disconnect" must call.
    public Task ReleaseDeviceAsync(BTDevice d) => RunLoggedAsync(() => Engine.ReleaseDeviceNowAsync(d), $"release {d.Name}");

    public Task PairDeviceAsync(BTDevice d) => RunLoggedAsync(() => Engine.PairDeviceNowAsync(d), $"pair {d.Name}");

    public Task UnpairDeviceAsync(BTDevice d) => RunLoggedAsync(() => Engine.UnpairDeviceNowAsync(d), $"unpair {d.Name}");

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
        if (reevaluate) Fire(Engine.ReevaluateAsync, "re-evaluate after config change");
        StateChanged?.Invoke();
    }

    /// Replace the whole config (e.g. after an import) and re-seed everything, including the
    /// system integrations that are keyed off config (hotkeys, launch-at-login).
    public void ReplaceConfig(AppConfig config)
    {
        Config = config.Normalized();
        Engine.Config = Config;
        _store.Save(Config);
        Engine.RefreshMonitoring();
        ApplyHotkeys();
        LoginItem.SetEnabled(Config.LaunchAtLogin);
        Fire(Engine.ReevaluateAsync, "re-evaluate after config import");
        StateChanged?.Invoke();
    }

    public void Save() => _store.SaveDebounced(Config);

    public string ConfigPath => _store.Path;
}