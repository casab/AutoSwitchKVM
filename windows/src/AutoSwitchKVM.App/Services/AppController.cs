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
    private readonly ConfigStore _store;
    private readonly PnpUsbMonitor _usb;
    private readonly WinRtBluetooth _bt;
    private readonly LogitechHostSwitcher _logitech = new();
    private readonly ZmkHostSwitcher _zmk = new();
    private readonly CompositeHostSwitcher _hostSwitcher;
    private readonly PowerMonitor _power = new();
    private readonly HotKeyService _hotkeys = new();
    private readonly ToastNotifier _toasts = new();
    private readonly DispatcherQueue _dispatcher;
    private Timer? _pollTimer;

    public AppConfig Config { get; private set; }
    public SelectionEngine Engine { get; }

    /// Raised (on the UI thread) whenever state the UI shows may have changed.
    public event Action? StateChanged;

    /// Preview mode (developer: `--ui-preview`): the UI runs against the real config, but nothing is
    /// started (no USB/Bluetooth/power/hotkeys) and nothing is written, so it can run next to the
    /// real app (e.g. for screenshots) without touching the devices or the settings.
    public bool IsPreview { get; }

    /// Preview only: fake a mix of device states and "selected" so every style can be seen.
    public bool IsDemo { get; }

    public AppController(bool preview = false, string? configPath = null, bool demo = false)
    {
        IsPreview = preview;
        IsDemo = preview && demo;
        _store = new ConfigStore(preview ? configPath : null);
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _usb = new PnpUsbMonitor(_dispatcher);   // marshal callbacks onto the UI thread
        _bt = new WinRtBluetooth(_dispatcher);
        _store.OnSaveError = ex => Log.Error("config", "config save failed", ex);
        Config = _store.Load();
        Log.MinLevel = LogLevels.Parse(Config.LogLevel);
        Log.Info("app", $"config loaded from {_store.Path}: profile='{Config.ActiveProfileName}', " +
            $"source={(Config.Source is null ? "(none)" : Config.Source.DisplayVidPid)}, devices={Config.Devices.Count}, paused={Config.Paused}");

        _hostSwitcher = new CompositeHostSwitcher(_logitech, _zmk);
        Engine = new SelectionEngine(Config, _usb, _bt, new AppLogSink(), _hostSwitcher);
        Engine.Changed += () => StateChanged?.Invoke();
        Engine.OnNotice = (title, body) =>
        {
            Log.Debug("notice", $"{title}: {body}");
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

    // ---- Device list changes (no full re-evaluation: that reconnected every other device) ----

    /// A device was added or re-enabled: connect just it if this PC is selected.
    public void DeviceAddedOrEnabled(Guid deviceId)
    {
        if (IsPreview) return;
        Fire(() => Engine.ConnectAddedDeviceAsync(deviceId), "connect added device");
    }

    /// A channel-switching device's channels (null when unsupported or not connected here).
    public async Task<HostChannels?> GetChannelsAsync(string address)
    {
        try { return await _hostSwitcher.GetChannelsAsync(address); }
        catch (Exception ex)
        {
            Log.Debug("hidpp", $"{address}: channel query failed ({ex.Message})");
            return null;
        }
    }

    /// The names a channel-switching device stores per channel (null when unsupported/unreachable).
    public async Task<IReadOnlyList<string?>?> GetChannelNamesAsync(string address)
    {
        try { return await _logitech.GetChannelNamesAsync(address); }
        catch (Exception ex)
        {
            Log.Debug("hidpp", $"{address}: channel names query failed ({ex.Message})");
            return null;
        }
    }

    /// "Channel 2 · MacBook" style labels for a channel choice: index 0 = automatic. A device that
    /// reports pairing per channel (the ZMK module) calls them profiles and flags the empty ones.
    public static IReadOnlyList<string> ChannelChoiceLabels(string automatic, IReadOnlyList<string?>? names, int count = 3, HostChannels? channels = null)
    {
        var labels = new List<string> { automatic };
        var noun = channels?.Paired is null ? "Channel" : "Profile";
        for (var i = 0; i < Math.Max(count, names?.Count ?? 0); i++)
        {
            var name = names is not null && i < names.Count ? names[i] : null;
            var label = name is null ? $"{noun} {i + 1}" : $"{noun} {i + 1} · {name}";
            if (channels?.Paired is { } paired && i < paired.Count && !paired[i]) label += " (not paired)";
            else if (channels is not null && i == channels.Current) label += " (this PC)";
            labels.Add(label);
        }
        return labels;
    }

    /// A device was removed: drop its status. Leaves the device as it is right now.
    public void DeviceRemoved(Guid deviceId) => Engine.ForgetDevice(deviceId);

    private static async Task RunLoggedAsync(Func<Task> work, string what)
    {
        try { await work(); }
        catch (Exception ex) { Log.Error("app", $"{what} failed unexpectedly", ex); }
    }

    public void Start()
    {
        Log.Info("app", $"starting; log file: {Log.FilePath}; log detail: {LogLevels.ToConfig(Log.MinLevel)}");
        _toasts.Register();

        _usb.Changed += OnUsbSnapshot;
        _usb.Start();
        Log.Debug("usb", "monitor started");
        Engine.RefreshMonitoring();
        Fire(Engine.SeedAsync, "initial seed");
        _pollTimer = new Timer(_ => _dispatcher.Enqueue("status poll", () => Fire(Engine.PollStatusesAsync, "status poll")),
            null, 10_000, 10_000);

        // Sleep/wake. Suspend runs on the SystemEvents thread and blocks it (bounded): an
        // enqueued-to-UI handler may never get pumped before the machine sleeps, and the unpair
        // is what frees the devices for the other host. NotifySuspending (thread-safe) first halts
        // the engine so it can't re-pair (e.g. an auto-reconnect after the "drop" our own release
        // causes). The release talks to the Bluetooth controller directly (the engine is
        // UI-thread-only); resume re-enables and re-seeds the engine so statuses reconcile.
        _power.OnSuspend = () => ReleaseBeforeGoingAway("suspend");
        // Log-off/shutdown: same release, so the device isn't left bonded here while this PC is off
        // (the next launch also reconciles: spec §3 Evaluate step 5).
        _power.OnSessionEnding = reason => ReleaseBeforeGoingAway(reason);
        _power.OnResume = () => _dispatcher.Enqueue("resume", () =>
        {
            Log.Info("power", "resume -> re-evaluate");
            // A release that couldn't even start before sleep (e.g. queued behind a pair holding
            // the radio lock) must not fire now, possibly after the other host paired: drop it and
            // let the re-seed decide (release if the source is absent, connect if present).
            Interlocked.Exchange(ref _awayReleaseCts, null)?.Cancel();
            Engine.NotifyResumed();
            Fire(Engine.ReevaluateAsync, "resume re-evaluate");
        });
        _power.Start();

        // Global hotkeys (WM_HOTKEY fires on this UI thread).
        _hotkeys.OnAction = a => _dispatcher.Enqueue("hotkey", () => HandleHotkey(a));
        _hotkeys.Start();
        ApplyHotkeys();
        Log.Debug("hotkey", $"global hotkeys {(Config.GlobalHotkeysEnabled ? "enabled" : "disabled")}");

        LoginItem.SetEnabled(Config.LaunchAtLogin);

        Log.Info("app", "started");
        if (SynchronizationContext.Current is not SafeSynchronizationContext)
            Log.Warn("app", $"UI thread context is {SynchronizationContext.Current?.GetType().Name ?? "none"}, not the guarded one: async void exceptions will crash the app");
    }

    /// Show a notification (callers check Config.ShowNotifications where it applies).
    public void Notify(string title, string body) => _toasts.Notify(title, body);

    /// Stop monitors and save. Does not release devices (an update restart keeps the bond).
    public void Shutdown(string reason = "user exit")
    {
        // Logged so an exit is distinguishable from a crash when reading the log afterwards.
        Log.Info("app", $"shutting down ({reason})");
        _pollTimer?.Dispose();
        _hotkeys.Dispose();
        _power.Dispose();
        _toasts.Unregister();
        _usb.Stop();
        _bt.StopMonitoring();
        if (!IsPreview) _store.Save(Config);
    }

    // ---- System integration ----

    private CancellationTokenSource? _awayReleaseCts;

    /// Runs on the SystemEvents thread for suspend / log-off / shutdown. Halts the engine first
    /// (thread-safe) so nothing re-pairs, then releases, blocking at most 1.5 s. Windows' grace is
    /// ~2 s; the release probe shows the link itself drops ~0.4–0.8 s into BluetoothRemoveDevice,
    /// so the part that frees the device for the other host fits even though the full call takes
    /// ~3.4 s.
    private void ReleaseBeforeGoingAway(string reason)
    {
        Engine.NotifySuspending();
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _awayReleaseCts, cts)?.Cancel();
        Log.Info("power", $"{reason} -> releasing enabled devices (bounded to 1.5 s)");
        try
        {
            // Task.Run keeps continuations off this thread's SynchronizationContext so the
            // bounded Wait cannot deadlock.
            var released = Task.Run(() => ReleaseEnabledDevicesAsync(cts.Token)).Wait(TimeSpan.FromMilliseconds(1500));
            Log.Info("power", released ? $"{reason} release complete" : $"{reason} release still running");
        }
        catch (Exception ex)
        {
            Log.Warn("power", $"{reason} release failed: {ex.Message}");
        }
    }

    /// Release every *enabled* device (disconnect + unpair where managed) — the going-away path.
    /// Talks to the Bluetooth controller directly so it can run off the UI thread; the engine
    /// reconciles statuses on resume. Cancelled on resume if it never got to run.
    private async Task ReleaseEnabledDevicesAsync(CancellationToken ct)
    {
        var devices = Config.Devices.Where(d => d.Enabled).ToList();
        foreach (var device in devices)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _bt.DisconnectAsync(device.Address, ct);
                if (device.ManagePairing) await _bt.UnpairAsync(device.Address, ct);
                Log.Debug("power", $"released {device.Name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (OperationCanceledException)
            {
                Log.Info("power", $"release {device.Name} cancelled after {sw.ElapsedMilliseconds} ms (resumed before it ran)");
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

    /// Settings ▸ Diagnostics ▸ Log detail. Applies immediately and persists.
    public void SetLogLevel(LogLevel level)
    {
        Mutate(c => c.LogLevel = LogLevels.ToConfig(level));
        Log.MinLevel = level;
        Log.Always("app", $"log detail set to {LogLevels.ToConfig(level)}");
    }

    public void SetLaunchAtLogin(bool enabled)
    {
        Config.LaunchAtLogin = enabled;
        Save();
        if (!IsPreview) LoginItem.SetEnabled(enabled);
        StateChanged?.Invoke();
    }

    private void OnUsbSnapshot(IReadOnlyList<UsbDeviceInfo> snapshot) => Engine.OnUsbSnapshot(snapshot);

    // ---- State for the UI ----

    public bool Paused => Config.Paused;
    public bool Selected => IsDemo || Engine.Selected;
    public bool BluetoothPowered => Engine.BluetoothPowered;

    public string StatusText =>
        Config.Paused ? "Paused"
        : !Engine.BluetoothPowered ? "Bluetooth off"
        : Engine.Selected ? "This PC is selected"
        : "Switched away";

    public DeviceStatus StatusFor(BTDevice device)
    {
        if (IsDemo)
        {
            var i = Config.Devices.FindIndex(d => d.Id == device.Id);
            // Screenshot mix (README): two connected, one connecting, then a problem and an idle one.
            return (i % 5) switch
            {
                0 or 1 => DeviceStatus.Connected,
                2 => DeviceStatus.Connecting,
                3 => DeviceStatus.Error("no response within 30 s"),
                _ => DeviceStatus.Disconnected,
            };
        }
        return Engine.Statuses.TryGetValue(device.Id, out var s) ? s : DeviceStatus.Idle;
    }

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

    // Handoff type per address (spec §5a). A device's type doesn't change; cache what was learned.
    private readonly Dictionary<string, HandoffAssessment> _handoff = new();

    /// How this device handles more than one computer, best effort (UI: Add device, device settings).
    public async Task<HandoffAssessment> ClassifyAsync(string address, string name)
    {
        var key = address.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (_handoff.TryGetValue(key, out var cached)) return cached;
        var identity = await _bt.GetIdentityAsync(address, name);
        var result = HandoffCatalog.Classify(identity);
        var cache = identity.VendorId is not null || identity.Kind != DeviceKind.Unknown;

        // Logitech multi-host devices may support HID++ "Change Host", and a ZMK keyboard may run
        // the AutoSwitch module (spec §5a type 5): ask the device. It only answers while connected
        // to this PC (and after a firmware update), so a "no answer" isn't cached.
        if (identity.VendorId == HandoffCatalog.Logitech || result.Type == HandoffType.DevicePicksHost)
        {
            HostChannels? channels = null;
            try { channels = await _hostSwitcher.GetChannelsAsync(address); }
            catch (Exception ex) { Log.Debug("hidpp", $"{name}: channel query failed ({ex.Message})"); }
            if (channels is not null)
                result = new HandoffAssessment(HandoffType.ChannelSwitch, HandoffConfidence.Known,
                    channels.Paired is null
                        ? $"{channels.Count} channels; this PC is channel {channels.Current + 1}."
                        : $"AutoSwitch firmware module: {channels.Count} Bluetooth profiles; this PC is profile {channels.Current + 1}.");
            else
                cache = false;   // maybe just not connected right now: ask again next time
        }

        // Only cache an answer backed by device data; a name-only guess may improve once paired.
        if (cache) _handoff[key] = result;
        Log.Debug("bt", $"handoff type {name} ({address}): {result.Type}, {result.Confidence} " +
            $"(vendor {identity.VendorId:X4}, product {identity.ProductId:X4}, {identity.Kind}, {(identity.IsLowEnergy ? "LE" : "Classic")})");
        return result;
    }
    /// Last reconciled USB snapshot (no WMI call): for UI refreshes that run on every state change.
    public IReadOnlyList<UsbDeviceInfo> LastUsbSnapshot => _usb.LastSnapshot;

    /// A fresh enumeration off the UI thread (WMI takes ~0.3–0.6 s).
    public Task<IReadOnlyList<UsbDeviceInfo>> UsbSnapshotAsync() => Task.Run(() => _usb.Snapshot());

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
        Log.MinLevel = LogLevels.Parse(Config.LogLevel);
        Engine.Config = Config;
        if (!IsPreview) _store.Save(Config);
        Engine.RefreshMonitoring();
        ApplyHotkeys();
        LoginItem.SetEnabled(Config.LaunchAtLogin);
        Fire(Engine.ReevaluateAsync, "re-evaluate after config import");
        StateChanged?.Invoke();
    }

    public void Save()
    {
        if (!IsPreview) _store.SaveDebounced(Config);
    }

    public string ConfigPath => _store.Path;
}