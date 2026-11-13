using System.Diagnostics;
using AutoSwitchKVM.Core.Models;

namespace AutoSwitchKVM.Core;

/// Core state machine: maps USB source presence to connect/disconnect of the enabled Bluetooth
/// devices. Ported from osx/Sources/AutoSwitchKVM/Engine/SelectionEngine.swift, plus failure
/// classification (SPECIFICATION.md §3 "Failure classification") and structured logging.
///
/// Threading: like the macOS engine (which is @MainActor), this assumes single-threaded use - the
/// app marshals calls onto the UI thread (DispatcherQueue). It does not lock internally. The one
/// exception is NotifySuspending, which is safe to call from the power-event thread.
///
/// Determinism for tests: the production debounce/poll timers are separate from the transition
/// logic. Tests drive HandleUsb/ApplyUsbSnapshot + EvaluateNowAsync directly (no timers), and set
/// SleepHook to a no-op so per-device delay, post-pair settle, and retry backoff are instant.
public sealed class SelectionEngine
{
    private const string Cat = "engine";

    private readonly IUsbMonitor _usb;
    private readonly IBluetoothController _bt;
    private readonly ILogSink _log;
    private readonly Random _rng = new();

    private readonly HashSet<ushort> _presentProductIDs = new();
    /// Presence the automation last acted on (a latch, distinct from Selected which tracks raw
    /// presence even while paused).
    private bool _automationActed;
    /// Bumps on each new run so in-flight work can detect it was superseded. Incremented with
    /// Interlocked because NotifySuspending may bump it from another thread.
    private int _runToken;
    /// Devices with an engine operation in flight (ref-counted: a manual action can overlap a
    /// superseded run that hasn't unwound yet). Status events/polls skip busy devices.
    private readonly Dictionary<Guid, int> _busyDevices = new();
    private readonly Dictionary<Guid, DeviceStatus> _statuses = new();
    private volatile bool _suspended;

    /// Set by a seed; the first unpaused evaluation after it reconciles (spec §3 Evaluate step 5).
    private bool _seedReconcilePending;
    /// Latest run that requested a release / a connect of each device. A pair that completes after a
    /// newer release (and no newer connect) is undone (spec §3). _globalReleaseToken is the suspend
    /// release (written from the power-event thread).
    private readonly Dictionary<Guid, int> _releaseRequestedToken = new();
    private readonly Dictionary<Guid, int> _connectRequestedToken = new();
    private int _globalReleaseToken;
    /// Engine unpairs in flight / last returned, per device: a pair waits for them (spec §3).
    private readonly Dictionary<Guid, Task> _unpairInFlight = new();
    private readonly Dictionary<Guid, DateTime> _lastUnpairReturned = new();

    private CancellationTokenSource? _debounceCts;

    /// The live configuration. The app may swap this on a profile switch (then call RefreshMonitoring
    /// + ReevaluateAsync). Tests mutate the same instance (e.g. Config.Paused = true).
    public AppConfig Config { get; set; }

    // ---- Observable state (UI binds to these; Changed fires after any update) ----
    public bool Selected { get; private set; }
    public bool BluetoothPowered { get; private set; } = true;
    public string LastReason { get; private set; } = "startup";
    public IReadOnlyDictionary<Guid, DeviceStatus> Statuses => _statuses;
    public event Action? Changed;

    /// Raised on connect/disconnect events for user-facing notices (title, body).
    public Action<string, string>? OnNotice;
    /// Raised when a managed device drops while the source is still present (not an engine action).
    public Action<BTDevice>? OnUnexpectedDisconnect;

    // ---- Test seams (internal; see InternalsVisibleTo) ----
    /// Replaceable delay used for per-device connect delay, post-pair settle, and retry backoff.
    internal Func<int, Task> SleepHook = ms => Task.Delay(ms);
    /// Settle delay after pairing before the clean reconnect (ms).
    internal int PostPairSettleMs = 800;
    /// Auto-reconnect scheduling (internal so tests can make it deterministic).
    internal int AutoReconnectDelayMs = 2000;
    internal int AutoReconnectMinIntervalSecs = 30;
    private readonly Dictionary<Guid, DateTime> _lastAutoReconnect = new();

    /// "Soft" failures (DeviceBusy / OperationInProgress) retry on this short fixed interval and
    /// don't consume connectRetryMax — the other host releasing the device takes seconds, and
    /// exponential backoff would overshoot it. Bounded by MaxSoftRetries (~60 s by default).
    internal int SoftRetryIntervalMs = 3000;
    internal int MaxSoftRetries = 20;
    /// Overrides Config.BtCallTimeoutSecs (tests use a few ms instead of whole seconds).
    internal TimeSpan? CallTimeoutOverride;
    /// Pause before the single retry of a failed release (unpair).
    internal int ReleaseRetryDelayMs = 1000;
    /// HidNotReady failures in one connect run before it ends with a power-cycle hint (spec §3).
    internal int MaxHidNotReady = 2;
    /// Spec §3 connect flow (values match macOS): wait this long for HID on the pairing link before
    /// falling back to drop + settle + connect; then wait up to HidReadyTimeoutMs for HID after the
    /// connect; poll every HidPollIntervalMs.
    internal int PostPairHidWaitMs = 3000;
    internal int HidReadyTimeoutMs = 5000;
    internal int HidPollIntervalMs = 250;
    internal Func<DateTime> Clock = () => DateTime.UtcNow;

    public SelectionEngine(AppConfig config, IUsbMonitor usb, IBluetoothController bt, ILogSink? log = null,
                           IHostSwitcher? hostSwitcher = null)
    {
        Config = config;
        _usb = usb;
        _bt = bt;
        _log = log ?? NullLogSink.Instance;
        _hostSwitcher = hostSwitcher;
    }

    private readonly IHostSwitcher? _hostSwitcher;

    // MARK: - Channel switching (spec §5a type 5)

    private bool _presenceEvaluated;                                   // Selected reflects a real evaluation
    private readonly HashSet<Guid> _channelMarked = new();             // marked our channel this session
    private readonly Dictionary<Guid, DateTime> _lastLateRelease = new();
    internal const int LateReleaseMinIntervalSecs = 10;

    /// A device that switches channel on request is sent to the other computer's channel instead
    /// of being unpaired. True when it was sent; false when it doesn't support it, isn't
    /// connected here, or the target channel is unknown (the caller then releases it as usual).
    private async Task<bool> TrySwitchChannelAsync(BTDevice device, string why)
    {
        if (_hostSwitcher is null) return false;
        HostChannels? ch;
        try { ch = await WithTimeoutAsync(ct => _hostSwitcher.GetChannelsAsync(device.Address, ct)); }
        catch (Exception ex)
        {
            Debug($"{device.Name}: channel query failed: {Classify(ex).Summary}");
            return false;
        }
        if (ch is null) return false;   // no channel switching, or not connected to this PC

        int? target = device.ReleaseToChannel is int n && n >= 1 && n <= ch.Count ? n - 1 : ch.MarkedOtherChannel;
        if (target is null)
        {
            Warn($"{device.Name}: it can switch channels, but the other computer's channel isn't known " +
                 "(set it in the device's settings, or run AutoSwitch on the other computer)");
            return false;
        }
        if (target == ch.Current)
        {
            Warn($"{device.Name}: the other computer's channel is set to {target + 1}, but that's this PC's channel; check the setting");
            return false;
        }
        try { await WithTimeoutAsync(ct => _hostSwitcher.SwitchToAsync(device.Address, target.Value, ct)); }
        catch (Exception ex)
        {
            Warn($"{device.Name}: switching to channel {target + 1} failed: {Classify(ex).Summary}");
            return false;
        }
        Info($"{device.Name}: sent to channel {target + 1} ({why})");
        return true;
    }

    /// A device became connected. Selected: mark our channel (once per session) so the other
    /// computer can find it. Not selected (the KVM is elsewhere, e.g. the keyboard was asleep at
    /// the switch and woke up on its old channel): send it on, rate-limited.
    private void OnBecameConnected(Guid id)
    {
        if (_hostSwitcher is null) return;
        var device = Config.Devices.FirstOrDefault(d => d.Id == id);
        if (device is null || !device.Enabled || device.ManagePairing) return;
        if (Selected)
        {
            if (!_channelMarked.Add(id)) return;
            _ = RunLoggedAsync(async () =>
            {
                try { await WithTimeoutAsync(ct => _hostSwitcher.MarkCurrentChannelAsync(device.Address, ct)); }
                catch (Exception ex)
                {
                    _channelMarked.Remove(id);
                    Debug($"{device.Name}: could not mark this PC's channel: {Classify(ex).Summary}");
                }
            }, $"{device.Name}: mark channel");
            return;
        }
        if (!_presenceEvaluated || Config.Paused || _suspended || IsBusy(id)) return;
        var now = DateTime.UtcNow;
        if (_lastLateRelease.TryGetValue(id, out var last) && (now - last).TotalSeconds < LateReleaseMinIntervalSecs) return;
        _lastLateRelease[id] = now;
        Info($"{device.Name}: connected while the KVM is on the other computer -> sending it there");
        _ = RunLoggedAsync(async () =>
        {
            MarkBusy(id);
            try { await TrySwitchChannelAsync(device, "it connected here while the KVM is elsewhere"); }
            finally { ClearBusy(id); }
        }, $"{device.Name}: late release");
    }

    /// A unit of engine work: the run token it belongs to plus what started it (for the log).
    private readonly record struct Run(int Token, string Trigger);

    private Run NewRun(string trigger) => new(Interlocked.Increment(ref _runToken), trigger);
    private Run CurrentRun(string trigger) => new(Volatile.Read(ref _runToken), trigger);

    /// Why in-flight work for this run must stop, or null if it may continue.
    private string? StaleReason(Run run, bool respectSelection)
    {
        if (_suspended) return "system is suspending";
        var current = Volatile.Read(ref _runToken);
        if (run.Token != current) return $"superseded by run #{current}";
        if (respectSelection && !Selected) return "source no longer present";
        return null;
    }

    // MARK: - Status monitoring

    /// (Re)register Bluetooth connect/disconnect observers for the current device set.
    public void RefreshMonitoring()
    {
        _bt.StartMonitoring(Config.Devices.Select(d => d.Address), HandleStatusEvent);
    }

    private void HandleStatusEvent(ConnectionChange change)
    {
        var device = Config.Devices.FirstOrDefault(d => d.AddressDigits == change.Address);
        if (device is null) return;
        if (IsBusy(device.Id)) return;
        UpdateStatus(device, change.IsConnected, "event");
    }

    /// Poll live connection state for all devices (safety net for a missed event). Refreshes adapter
    /// power first; skips devices that are mid-transition.
    public async Task PollStatusesAsync()
    {
        await RefreshPowerAsync();
        foreach (var device in Config.Devices.ToList())
        {
            if (IsBusy(device.Id)) continue;
            if (!BluetoothPowered)
            {
                SetStatus(device.Id, DeviceStatus.BluetoothOff);
                continue;
            }
            bool connected;
            try { connected = await _bt.IsConnectedAsync(device.Address); }
            catch (Exception ex)
            {
                Warn($"{device.Name}: status poll failed: {Classify(ex).Summary}");
                continue;
            }
            if (IsBusy(device.Id)) continue;  // an operation started while we were querying
            UpdateStatus(device, connected, "poll");
        }
    }

    /// Reflect a device's live connection state, and flag a passive drop (connected -> disconnected
    /// while the source is present, BT is on, and we're not mid-transition) as unexpected.
    private void UpdateStatus(BTDevice device, bool connected, string origin)
    {
        var old = GetStatus(device.Id);
        var @new = connected ? DeviceStatus.Connected : DeviceStatus.Disconnected;
        if (old != @new)
        {
            if (old is null) Debug($"{device.Name}: initial status {@new.Label} ({origin})");
            else Info($"{device.Name}: {old.Value.Label} -> {@new.Label} (reported by {origin})");
            SetStatus(device.Id, @new);
        }

        if (!connected && old == DeviceStatus.Connected && Selected && BluetoothPowered && device.Enabled && !_suspended)
        {
            Warn($"{device.Name}: dropped unexpectedly while this PC is selected");
            OnUnexpectedDisconnect?.Invoke(device);
            MaybeAutoReconnect(device);
        }
    }

    /// One bounded, rate-limited reconnect after an unexpected drop (opt-out via
    /// AutoReconnectOnDrop). Uses the current run token so any newer run supersedes it;
    /// combined with the retry escalation (re-pair from scratch on retry) this self-heals even
    /// when the device stopped honoring its bond.
    private void MaybeAutoReconnect(BTDevice device)
    {
        if (!Config.AutoReconnectOnDrop || Config.Paused || _suspended) return;
        if (IsBusy(device.Id)) return;
        var now = DateTime.UtcNow;
        if (_lastAutoReconnect.TryGetValue(device.Id, out var last) &&
            (now - last).TotalSeconds < AutoReconnectMinIntervalSecs)
        {
            Debug($"{device.Name}: auto-reconnect skipped (rate limit: last one {(now - last).TotalSeconds:0}s ago)");
            return;
        }
        _lastAutoReconnect[device.Id] = now;
        Info($"{device.Name}: auto-reconnect scheduled in {AutoReconnectDelayMs} ms");
        var run = CurrentRun("auto-reconnect after drop");
        _ = RunLoggedAsync(() => AutoReconnectAsync(device, run), $"{device.Name}: auto-reconnect");
    }

    private async Task AutoReconnectAsync(BTDevice device, Run run)
    {
        await SleepHook(AutoReconnectDelayMs);
        string? skip = StaleReason(run, respectSelection: true)
            ?? (!BluetoothPowered ? "Bluetooth is off"
                : Config.Paused ? "automation paused"
                : IsBusy(device.Id) ? "another operation is in flight"
                : null);
        if (skip is not null)
        {
            Debug($"{device.Name}: auto-reconnect cancelled: {skip}");
            return;
        }
        MarkBusy(device.Id);
        try { await ConnectOneAsync(device, run, respectSelection: true); }
        finally { ClearBusy(device.Id); }
    }

    /// Refresh adapter power; on a false->true transition, re-seed so devices reconnect.
    public async Task RefreshPowerAsync()
    {
        bool? reported;
        try { reported = await _bt.IsPoweredOnAsync(); }
        catch (Exception ex)
        {
            Warn($"adapter power query failed: {Classify(ex).Summary}");
            reported = null;
        }
        var powered = reported ?? true;
        if (powered == BluetoothPowered) return;
        BluetoothPowered = powered;
        Info($"Bluetooth adapter is now {(powered ? "ON" : "OFF")}");
        RaiseChanged();
        if (powered) await SeedAsync();
    }

    // MARK: - System suspend / resume

    /// The system is about to sleep. Halts automation and supersedes in-flight runs so nothing
    /// re-pairs while the app releases devices for the other host. Safe from any thread.
    public void NotifySuspending()
    {
        _suspended = true;
        var token = Interlocked.Increment(ref _runToken);
        // The app releases the devices next; a pair still in flight must be undone when it lands.
        Volatile.Write(ref _globalReleaseToken, token);
        Info($"system suspending: automation halted, in-flight work superseded (run #{token})");
    }

    /// The system woke up. Call ReevaluateAsync afterwards to reconcile.
    public void NotifyResumed()
    {
        _suspended = false;
        Info("system resumed: automation re-enabled");
    }

    // MARK: - Seeding & USB input

    /// Establish presence from scratch (launch, wake, profile switch) and schedule a debounced
    /// evaluation — the macOS seed() behavior, so a KVM re-enumeration storm at seed time gets
    /// the same debounce as any other USB activity. Tests drive EvaluateNowAsync directly.
    ///
    /// The USB snapshot is taken off the calling (UI) thread: on Windows it is a synchronous WMI
    /// enumeration (~0.3–0.6 s). If it fails (e.g. WMI not ready right after wake), the previous
    /// presence is kept and the evaluation still runs, instead of leaving the engine with cleared
    /// state and nothing scheduled.
    public async Task SeedAsync()
    {
        var source = Config.Source;
        IReadOnlyList<UsbDeviceInfo>? snapshot = null;
        if (source is not null)
        {
            try { snapshot = await Task.Run(() => _usb.Snapshot()); }
            catch (Exception ex)
            {
                Warn($"seed: USB snapshot failed ({Classify(ex).Message}); keeping the previous presence");
            }
        }

        _automationActed = false;
        _seedReconcilePending = true;
        if (source is null)
        {
            _presentProductIDs.Clear();
        }
        else if (snapshot is not null)
        {
            _presentProductIDs.Clear();
            foreach (var d in snapshot)
                if (d.VendorID == source.VendorID && source.ProductIDs.Contains(d.ProductID))
                    _presentProductIDs.Add(d.ProductID);
        }
        LastReason = "initial scan";
        Debug(source is null
            ? "seed: no source configured"
            : $"seed: source '{source.Name}' {(_presentProductIDs.Count > 0 ? "present" : "absent")}");
        ScheduleEvaluate();
    }

    public Task ReevaluateAsync() => SeedAsync();

    /// Recompute presence from a full USB snapshot (the Windows reconcile path). Pure: call
    /// EvaluateNowAsync (or OnUsbSnapshotAsync) to act on it.
    public void ApplyUsbSnapshot(IReadOnlyList<UsbDeviceInfo> snapshot)
    {
        var wasPresent = _presentProductIDs.Count > 0;
        _presentProductIDs.Clear();
        var source = Config.Source;
        if (source is not null)
        {
            foreach (var d in snapshot)
                if (d.VendorID == source.VendorID && source.ProductIDs.Contains(d.ProductID))
                    _presentProductIDs.Add(d.ProductID);
        }
        var present = _presentProductIDs.Count > 0;
        if (present != wasPresent)
        {
            LastReason = present ? "source appeared in USB snapshot" : "source left USB snapshot";
            Info($"usb: {LastReason}");
        }
        else
        {
            LastReason = "usb snapshot";
        }
    }

    /// Incremental USB event (mirrors the macOS handleUSB). Pure: updates presence only.
    public void HandleUsb(ushort vendorId, ushort productId, bool added)
    {
        var source = Config.Source;
        if (source is null || vendorId != source.VendorID || !source.ProductIDs.Contains(productId))
            return;

        if (added)
        {
            _presentProductIDs.Add(productId);
            LastReason = $"source arrived (0x{productId:X4})";
        }
        else
        {
            _presentProductIDs.Remove(productId);
            LastReason = $"source removed (0x{productId:X4})";
        }
    }

    // ---- Production entry points: update presence then debounce-evaluate ----

    public void OnUsbSnapshot(IReadOnlyList<UsbDeviceInfo> snapshot)
    {
        ApplyUsbSnapshot(snapshot);
        ScheduleEvaluate();
    }

    public void OnUsbEvent(ushort vendorId, ushort productId, bool added)
    {
        HandleUsb(vendorId, productId, added);
        ScheduleEvaluate();
    }

    /// Debounced evaluate: arrival debounce when the source is present (snappier connect), departure
    /// debounce when gone. Cancels any pending evaluate.
    public void ScheduleEvaluate()
    {
        _debounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        var ms = _presentProductIDs.Count == 0 ? Config.DebounceMs : Config.ArrivalDebounceMs;
        _ = RunLoggedAsync(() => DebouncedEvaluateAsync(ms, cts.Token), "debounced evaluate");
    }

    private async Task DebouncedEvaluateAsync(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;
        await EvaluateNowAsync();
    }

    /// The transition logic, awaitable so tests can drive it deterministically without timers.
    public async Task EvaluateNowAsync()
    {
        var present = _presentProductIDs.Count > 0;
        Selected = present;                 // always reflect real presence for the UI
        _presenceEvaluated = true;
        RaiseChanged();

        var wouldAct = present != _automationActed || (!present && _seedReconcilePending);
        if (_suspended)
        {
            if (wouldAct) Info($"evaluate: system suspending, not acting on '{LastReason}'");
            return;
        }
        if (Config.Paused)                  // automation suspended; don't act
        {
            if (wouldAct) Info($"evaluate: automation paused, not acting on '{LastReason}'");
            return;
        }

        // Only the first unpaused evaluation after a seed reconciles (spec §3 Evaluate step 5).
        var reconcile = _seedReconcilePending;
        _seedReconcilePending = false;

        if (!present && !_automationActed && reconcile)
        {
            var run = NewRun("reconcile: source absent after seed");
            Info($"run #{run.Token}: source absent after a seed -> releasing anything this PC may still hold");
            await ReleaseHeldAsync(run);
            return;
        }

        if (present && !_automationActed)
        {
            _automationActed = true;
            var run = NewRun($"auto: {LastReason}");
            Info($"run #{run.Token}: source present ({LastReason}) -> connecting {Config.Devices.Count(d => d.Enabled)} enabled device(s)");
            await ConnectAllAsync(run);
        }
        else if (!present && _automationActed)
        {
            _automationActed = false;
            var run = NewRun($"auto: {LastReason}");
            Info($"run #{run.Token}: source gone ({LastReason}) -> releasing {Config.Devices.Count(d => d.Enabled)} enabled device(s)");
            await DisconnectAllAsync(run);
        }
    }

    // MARK: - Manual actions (ignore selection; supersede in-flight automation)

    public async Task ConnectAllNowAsync()
    {
        var run = NewRun("manual: connect all");
        Info($"run #{run.Token}: manual connect all ({Config.Devices.Count} device(s))");
        foreach (var device in Config.Devices.ToList())
        {
            if (StaleReason(run, respectSelection: false) is { } why)
            {
                Debug($"run #{run.Token}: connect all stopped: {why}");
                return;
            }
            MarkBusy(device.Id);
            try { await ConnectOneAsync(device, run, respectSelection: false); }
            finally { ClearBusy(device.Id); }
        }
    }

    public async Task DisconnectAllNowAsync()
    {
        var run = NewRun("manual: disconnect all");  // invalidates any in-flight connect run
        Info($"run #{run.Token}: manual disconnect all ({Config.Devices.Count} device(s))");
        foreach (var device in Config.Devices.ToList())
        {
            if (StaleReason(run, respectSelection: false) is { } why)
            {
                Debug($"run #{run.Token}: disconnect all stopped: {why}");
                return;
            }
            MarkBusy(device.Id);
            try { await DisconnectOneAsync(device, run); }
            finally { ClearBusy(device.Id); }
        }
    }

    /// Per-device manual connect: the full connect flow (retries included), ignoring selection.
    public async Task ConnectDeviceNowAsync(BTDevice device)
    {
        var run = BeginManual(device, "manual connect");
        MarkBusy(device.Id);
        try { await ConnectOneAsync(device, run, respectSelection: false); }
        finally { ClearBusy(device.Id); }
    }

    /// A device was added to the profile or re-enabled. Connect just that device if this PC is
    /// selected and automation is running; the other devices are left alone. (Re-seeding the
    /// whole engine here ran a full connect pass, and every managePairing device went through
    /// pair + HID check again: "Connecting…" on devices that were working.) Joins the current run,
    /// so a source change still supersedes it.
    public async Task ConnectAddedDeviceAsync(Guid deviceId)
    {
        var device = Config.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (device is null || !device.Enabled) return;
        string? skip = !Selected ? "this PC is not selected"
            : Config.Paused ? "automation paused"
            : _suspended ? "system is suspending"
            : !BluetoothPowered ? "Bluetooth is off"
            : IsBusy(device.Id) ? "another operation is in flight"
            : null;
        if (skip is not null)
        {
            Debug($"{device.Name}: added/enabled; not connecting now ({skip})");
            return;
        }
        Info($"{device.Name}: added/enabled while this PC is selected -> connecting");
        var run = CurrentRun("device added/enabled");
        MarkBusy(device.Id);
        try { await ConnectOneAsync(device, run, respectSelection: true); }
        finally { ClearBusy(device.Id); }
    }

    /// A device was removed from the profile: forget its status. Nothing is connected or
    /// released (macOS behaves the same); the caller refreshes monitoring.
    public void ForgetDevice(Guid deviceId)
    {
        _statuses.Remove(deviceId);
        _lastAutoReconnect.Remove(deviceId);
    }

    /// Per-device manual release: disconnect, plus unpair for managePairing devices.
    public async Task ReleaseDeviceNowAsync(BTDevice device)
    {
        var run = BeginManual(device, "manual release");
        MarkBusy(device.Id);
        try { await DisconnectOneAsync(device, run); }
        finally { ClearBusy(device.Id); }
    }

    /// Raw single pair (Settings "Pair" test button).
    public Task PairDeviceNowAsync(BTDevice device) =>
        RawOpAsync(device, "pair", ct => _bt.PairAsync(device.Address, ct), releases: false);

    /// Raw single unpair (Settings "Unpair" test button).
    public Task UnpairDeviceNowAsync(BTDevice device) =>
        RawOpAsync(device, "unpair", ct => _bt.UnpairAsync(device.Address, ct), releases: true);

    /// A manual action on a device the engine is already working on supersedes that work (the
    /// user took control). Otherwise it joins the current run, so a later automation run (e.g.
    /// the source leaving) still supersedes it.
    private Run BeginManual(BTDevice device, string what)
    {
        if (IsBusy(device.Id))
        {
            var run = NewRun(what);
            Debug($"{device.Name}: {what} supersedes the in-flight operation (run #{run.Token})");
            return run;
        }
        return CurrentRun(what);
    }

    private async Task RawOpAsync(BTDevice device, string op, Func<CancellationToken, Task> call, bool releases)
    {
        var run = BeginManual(device, $"manual {op}");
        MarkBusy(device.Id);
        try
        {
            var sw = Stopwatch.StartNew();
            Debug($"{device.Name}: manual {op} started");
            try
            {
                if (releases)
                {
                    NoteIntent(_releaseRequestedToken, device.Id, run.Token);
                    await UnpairTrackedAsync(device);
                }
                else
                {
                    NoteIntent(_connectRequestedToken, device.Id, run.Token);
                    await WaitForUnpairGateAsync(device, run, respectSelection: false);
                    await WithTimeoutAsync(call);
                }
            }
            catch (Exception ex)
            {
                var f = Classify(ex);
                Warn($"{device.Name}: manual {op} failed after {Ms(sw)}: {f.Summary}");
                SetStatus(device.Id, DeviceStatus.Error(f.Message));
                return;
            }
            Info($"{device.Name}: manual {op} OK ({Ms(sw)})");

            // Set the status explicitly while still busy: a late "disconnected" event from the
            // stack then matches it instead of looking like an unexpected drop (which would
            // trigger an auto-reconnect that undoes the user's unpair).
            if (releases)
            {
                SetStatus(device.Id, DeviceStatus.Disconnected);
            }
            else
            {
                var connected = await SafeIsConnectedAsync(device);
                SetStatus(device.Id, connected ? DeviceStatus.Connected : DeviceStatus.Disconnected);
            }
        }
        finally { ClearBusy(device.Id); }
    }

    // MARK: - Retry backoff

    private const double MaxBackoffSeconds = 30.0;

    /// Backoff (seconds) between connect retries: base * 2^(attempt-1), capped. With base 2 and a
    /// 1-based attempt: 2, 4, 8, 16, 30 (capped). Deterministic.
    public static double BackoffSeconds(int @base, int attempt)
    {
        var value = @base * Math.Pow(2.0, attempt - 1);
        return Math.Min(value, MaxBackoffSeconds);
    }

    /// Backoff in milliseconds with +/-15% jitter (used between connect retries). The 30 s cap
    /// applies after jitter — the spec's cap bounds the real sleep.
    internal int BackoffMillisJittered(int @base, int attempt)
    {
        var jittered = BackoffSeconds(@base, attempt) * (0.85 + _rng.NextDouble() * 0.30);
        var capped = Math.Min(jittered, MaxBackoffSeconds);
        return (int)Math.Max(0, capped * 1000.0);
    }

    // MARK: - Actions

    private async Task ConnectAllAsync(Run run)
    {
        foreach (var device in Config.Devices.ToList())
        {
            if (!device.Enabled) continue;
            if (StaleReason(run, respectSelection: false) is { } why)
            {
                Debug($"run #{run.Token}: connect run stopped: {why}");
                return;
            }
            MarkBusy(device.Id);
            try { await ConnectOneAsync(device, run, respectSelection: true); }
            finally { ClearBusy(device.Id); }
        }
    }

    /// The connect flow for one device (SPECIFICATION.md §3), with failure classification:
    ///   - Soft failures (DeviceBusy, OperationInProgress) retry every SoftRetryIntervalMs and do
    ///     not consume connectRetryMax (bounded by MaxSoftRetries).
    ///   - Hard failures consume an attempt and back off exponentially. For managePairing devices,
    ///     a hard failure that may mean a stale bond (AuthenticationFailed / Timeout / Unknown)
    ///     makes the next try unpair first and re-pair from scratch.
    ///   - InvalidAddress gives up immediately; AdapterUnavailable refreshes adapter power.
    private async Task ConnectOneAsync(BTDevice device, Run run, bool respectSelection)
    {
        var cfg = Config;
        var addr = device.Address;
        var name = device.Name;
        var maxHard = Math.Max(1, cfg.ConnectRetryMax);
        var total = Stopwatch.StartNew();

        Debug($"{name}: connect started (run #{run.Token}, {run.Trigger}; managePairing={device.ManagePairing})");
        NoteIntent(_connectRequestedToken, device.Id, run.Token);
        SetStatus(device.Id, DeviceStatus.Connecting);

        // Optional per-device stagger before connecting.
        if (device.ConnectDelayMs > 0)
        {
            await SleepHook(device.ConnectDelayMs);
            if (StaleReason(run, respectSelection) is { } why)
            {
                Debug($"{name}: connect aborted during the {device.ConnectDelayMs} ms connect delay: {why}");
                MarkAborted(device.Id);
                return;
            }
        }

        var tries = 0;
        var hardFailures = 0;
        var softRetries = 0;
        var hidNotReady = 0;
        var wedged = false;
        var freshBond = false;
        Failure last = default;

        while (true)
        {
            if (!BluetoothPowered)
            {
                Info($"{name}: connect stopped: Bluetooth is off");
                SetStatus(device.Id, DeviceStatus.BluetoothOff);
                return;
            }
            if (StaleReason(run, respectSelection) is { } stale)
            {
                Debug($"{name}: connect aborted before try {tries + 1}: {stale}");
                MarkAborted(device.Id);
                return;
            }
            tries++;

            var attempt = Stopwatch.StartNew();
            var step = "status check";
            Failure failure;
            try
            {
                async Task Step(string label, Func<CancellationToken, Task> op)
                {
                    step = label;
                    var sw = Stopwatch.StartNew();
                    await WithTimeoutAsync(op);
                    Debug($"{name}: try {tries}: {label} OK ({Ms(sw)})");
                }

                if (!device.ManagePairing && await SafeIsConnectedAsync(device)
                    && await SafeHidReadyAsync(device) != false)
                {
                    Info($"{name}: already connected");
                    SetStatus(device.Id, DeviceStatus.Connected);
                    return;
                }

                var hidOnPairingLink = false;
                var paired = false;   // this try created/confirmed a bond (undo it if a release overtakes us)
                if (device.ManagePairing)
                {
                    if (freshBond)
                    {
                        Debug($"{name}: try {tries}: removing a possibly stale bond before re-pairing");
                        await BestEffortUnpairAsync(device, "stale-bond unpair");
                        freshBond = false;  // once per hard failure; soft retries in between don't repeat it
                    }

                    // Spec §3: never pair while an unpair of this device is in flight (a superseded
                    // release, e.g. a KVM bounce) or within ~2 s of one returning. On Windows,
                    // WinRT still reported IsPaired=True 17 ms after BluetoothRemoveDevice returned
                    // (2026-11-12 22:58:57), so the pair "succeeded" without pairing.
                    step = "unpair gate";
                    if (await WaitForUnpairGateAsync(device, run, respectSelection) is { } gateStale)
                    {
                        Debug($"{name}: connect aborted before pairing: {gateStale}");
                        MarkAborted(device.Id);
                        return;
                    }

                    await Step("pair", ct => _bt.PairAsync(addr, ct));
                    paired = true;

                    // A release may have been requested while we paired; don't go on to connect
                    // (on Windows: pair again), and undo the bond if a release overtook us.
                    if (StaleReason(run, respectSelection) is { } afterPair)
                    {
                        await AbortAfterPairAsync(device, run, $"after pairing: {afterPair}");
                        return;
                    }

                    // Spec §3: keep the pairing link and wait for HID on it; drop and reconnect only
                    // without it. (macOS, 2026-11-12: tearing the pairing link down while the stack
                    // was bringing HID up wedged the trackpad. Windows never closed it — its
                    // post-pair disconnect is a no-op — but skipping the drop + settle when HID is
                    // already up saves ~0.8 s per arrival.)
                    step = "post-pair HID";
                    if (await HidUpAfterPairAsync(device, addr, run, respectSelection))
                    {
                        hidOnPairingLink = true;
                    }
                    else
                    {
                        await BestEffortAsync(device, "post-pair disconnect", ct => _bt.DisconnectAsync(addr, ct));
                        await SleepHook(PostPairSettleMs);
                    }
                    if (StaleReason(run, respectSelection) is { } afterSettle)
                    {
                        await AbortAfterPairAsync(device, run, $"after the post-pair HID wait: {afterSettle}");
                        return;
                    }
                }

                if (!hidOnPairingLink)
                {
                    await Step("connect", ct => _bt.ConnectAsync(addr, ct));
                    paired |= device.ManagePairing;

                    // A clean connect is necessary but not sufficient: IsConnected must agree.
                    step = "verify";
                    if (!await SafeIsConnectedAsync(device))
                        throw new BluetoothException("connect completed but the device does not report connected");
                }

                // A link alone is not success: the device's HID input must be up (null = the
                // backend can't tell, e.g. not a HID peripheral; then the link counts).
                step = "HID";
                var hid = await WaitForHidAsync(device, addr, run, respectSelection);
                if (StaleReason(run, respectSelection) is { } afterHid)
                {
                    if (paired) await AbortAfterPairAsync(device, run, $"after the HID wait: {afterHid}");
                    else { Debug($"{name}: connect aborted after the HID wait: {afterHid}"); MarkAborted(device.Id); }
                    return;
                }
                if (hid == false)
                {
                    throw new BluetoothException(BluetoothFailureKind.HidNotReady,
                        $"link is up but the HID input never started within {HidReadyTimeoutMs / 1000.0:0.#} s");
                }

                Info($"{name}: connected after {Plural(tries, "try", "tries")} in {Ms(total)}" +
                     (hidOnPairingLink ? " (HID on the pairing link)" : ""));
                SetStatus(device.Id, DeviceStatus.Connected);
                OnNotice?.Invoke("Connected", name);
                return;
            }
            catch (Exception ex)
            {
                failure = Classify(ex);
            }

            last = failure;
            var tryLine = $"{name}: try {tries}: {step} failed after {Ms(attempt)}: {failure.Summary}";
            if (failure.Kind is BluetoothFailureKind.DeviceBusy or BluetoothFailureKind.OperationInProgress) Debug(tryLine);
            else Warn(tryLine);
            SetStatus(device.Id, DeviceStatus.Error(failure.Message));

            // Don't trust a link-only state; drop any partial connection so the next try is clean.
            await BestEffortAsync(device, "cleanup disconnect", ct => _bt.DisconnectAsync(addr, ct));

            // Superseded while the try was running: stop here rather than log a misleading retry.
            if (StaleReason(run, respectSelection) is { } afterFailure)
            {
                Debug($"{name}: connect aborted after failed try {tries}: {afterFailure}");
                MarkAborted(device.Id);
                return;
            }

            if (failure.Kind == BluetoothFailureKind.InvalidAddress)
                break;  // retrying can never help
            if (failure.Kind == BluetoothFailureKind.HidNotReady && ++hidNotReady >= MaxHidNotReady)
            {
                // A device whose HID never starts is wedged (seen on hardware: only a power cycle
                // recovered it). More pair/unpair churn doesn't help and may make it worse.
                wedged = true;
                break;
            }
            if (failure.Kind == BluetoothFailureKind.AdapterUnavailable)
                await RefreshPowerAsync();  // the loop top then stops with BluetoothOff if it's off

            var soft = failure.Kind is BluetoothFailureKind.DeviceBusy or BluetoothFailureKind.OperationInProgress;
            if (soft && softRetries < MaxSoftRetries)
            {
                softRetries++;
                Debug($"{name}: {SoftHint(failure.Kind)}; retrying in {SoftRetryIntervalMs} ms " +
                     $"(soft retry {softRetries}/{MaxSoftRetries}, not counted against the {maxHard} attempts)");
                await SleepHook(SoftRetryIntervalMs);
                continue;
            }
            if (soft)
                Warn($"{name}: soft-retry budget ({MaxSoftRetries}) exhausted; counting failures as hard from now on");

            hardFailures++;
            if (hardFailures >= maxHard)
                break;

            freshBond = device.ManagePairing && failure.Kind is BluetoothFailureKind.AuthenticationFailed
                or BluetoothFailureKind.Timeout or BluetoothFailureKind.Unknown;
            var backoff = BackoffMillisJittered(cfg.ConnectRetrySecs, hardFailures);
            Debug($"{name}: attempt {hardFailures}/{maxHard} failed; retrying in {backoff} ms" +
                 (freshBond ? " (will re-pair from scratch)" : ""));
            await SleepHook(backoff);
        }

        var cause = wedged
            ? "its input never started; turn the device off and on"
            : last.Message ?? "unknown error";
        Log(LogLevel.Error, $"{name}: gave up after {Plural(tries, "try", "tries")} in {Ms(total)} " +
            $"({hardFailures} hard, {softRetries} soft{(wedged ? $", {hidNotReady} HID-not-ready: device looks wedged" : "")}); " +
            $"last error: {last.Summary}");
        SetStatus(device.Id, DeviceStatus.Error($"gave up after {Plural(tries, "try", "tries")}: {cause}"));
        OnNotice?.Invoke("Connection failed", $"{name}: gave up after {Plural(tries, "try", "tries")} - {cause}");
    }

    /// Spec §3 Evaluate step 5: after a seed with the source absent, release what this host may
    /// still hold: disconnect connected devices, unpair managePairing ones (idempotent). Quiet:
    /// no "Disconnected" notice unless the device was actually connected.
    private async Task ReleaseHeldAsync(Run run)
    {
        foreach (var device in Config.Devices.ToList())
        {
            if (!device.Enabled) continue;
            if (StaleReason(run, respectSelection: false) is { } why)
            {
                Debug($"run #{run.Token}: reconcile stopped: {why}");
                return;
            }
            var connected = await SafeIsConnectedAsync(device);
            if (!device.ManagePairing && !connected) continue;   // nothing held
            MarkBusy(device.Id);
            try { await DisconnectOneAsync(device, run, notify: connected); }
            finally { ClearBusy(device.Id); }
        }
    }

    private async Task DisconnectAllAsync(Run run)
    {
        foreach (var device in Config.Devices.ToList())
        {
            if (!device.Enabled) continue;
            if (StaleReason(run, respectSelection: false) is { } why)  // e.g. the source came back
            {
                Debug($"run #{run.Token}: release run stopped: {why}");
                return;
            }
            MarkBusy(device.Id);
            try { await DisconnectOneAsync(device, run); }
            finally { ClearBusy(device.Id); }
        }
    }

    /// Tear down one device: disconnect, then (managePairing) unpair. Staleness is re-checked
    /// after every await — a newer run owns the devices once it starts, and a stale release must
    /// not undo the connect/pair the new run just performed. A failed unpair is retried once:
    /// the bond is what blocks the other host, so a silent release failure strands the device.
    /// notify=false: the quiet reconcile-on-seed release (spec §3 Evaluate step 5) — no
    /// "Disconnected" notice for a device that wasn't actually connected.
    private async Task DisconnectOneAsync(BTDevice device, Run run, bool notify = true)
    {
        var addr = device.Address;
        var name = device.Name;
        var total = Stopwatch.StartNew();
        Debug($"{name}: release started (run #{run.Token}, {run.Trigger}; managePairing={device.ManagePairing})");
        NoteIntent(_releaseRequestedToken, device.Id, run.Token);

        // Spec §5a type 5: send a channel-switching device to the other computer's channel; its
        // pairings stay as they are. Falls through to the usual release when that isn't possible.
        if (!device.ManagePairing && await TrySwitchChannelAsync(device, "the KVM left this PC"))
        {
            SetStatus(device.Id, DeviceStatus.Disconnected);
            if (notify) OnNotice?.Invoke("Switched", $"{name} went to the other computer");
            return;
        }

        Failure? disconnectError = null;
        try { await WithTimeoutAsync(ct => _bt.DisconnectAsync(addr, ct)); }
        catch (Exception ex)
        {
            disconnectError = Classify(ex);
            Warn($"{name}: disconnect failed: {disconnectError.Value.Summary}" +
                 (device.ManagePairing ? "; unpairing anyway" : ""));
        }
        if (StaleReason(run, respectSelection: false) is { } stale)
        {
            Debug($"{name}: release abandoned after disconnect: {stale}");
            return;
        }

        // Release the bond even if the disconnect call failed — the bond is what blocks the
        // other host (Magic Trackpad), and unpair tears the link down anyway.
        if (device.ManagePairing)
        {
            Failure? unpairError = null;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    await UnpairTrackedAsync(device);
                    unpairError = null;
                    break;
                }
                catch (Exception ex)
                {
                    unpairError = Classify(ex);
                    if (StaleReason(run, respectSelection: false) is { } why)
                    {
                        Debug($"{name}: release abandoned after failed unpair ({unpairError.Value.Summary}): {why}");
                        return;
                    }
                    if (attempt == 1 && unpairError.Value.Kind != BluetoothFailureKind.InvalidAddress)
                    {
                        Warn($"{name}: unpair failed: {unpairError.Value.Summary}; retrying once in {ReleaseRetryDelayMs} ms");
                        await SleepHook(ReleaseRetryDelayMs);
                        if (StaleReason(run, respectSelection: false) is { } why2)
                        {
                            Debug($"{name}: release abandoned before unpair retry: {why2}");
                            return;
                        }
                        continue;
                    }
                    break;
                }
            }
            if (StaleReason(run, respectSelection: false) is { } stale2)
            {
                Debug($"{name}: release abandoned after unpair: {stale2}");
                return;
            }
            if (unpairError is { } err)
            {
                Log(LogLevel.Error, $"{name}: release FAILED after {Ms(total)}: {err.Summary} - " +
                    "the device may still be bonded to this PC, so the other host cannot take it");
                SetStatus(device.Id, DeviceStatus.Error(err.Message));
                OnNotice?.Invoke("Release failed", $"{name}: {err.Message}");
                return;
            }
            Info($"{name}: released (unpaired) in {Ms(total)}");
            SetStatus(device.Id, DeviceStatus.Disconnected);
            if (notify) OnNotice?.Invoke("Disconnected", name);
            return;
        }

        if (disconnectError is { } dErr)
        {
            SetStatus(device.Id, DeviceStatus.Error(dErr.Message));
        }
        else
        {
            Info($"{name}: disconnected in {Ms(total)}");
            SetStatus(device.Id, DeviceStatus.Disconnected);
            if (notify) OnNotice?.Invoke("Disconnected", name);
        }
    }

    // MARK: - Helpers

    /// A classified failure: what kind (drives retry policy) and the human message.
    private readonly record struct Failure(BluetoothFailureKind Kind, string Message)
    {
        public string Summary => $"[{Kind}] {Message}";
    }

    private static Failure Classify(Exception ex) => ex switch
    {
        BluetoothException b => new Failure(b.Kind, b.Message),
        TimeoutException t => new Failure(BluetoothFailureKind.Timeout, t.Message),
        _ => new Failure(BluetoothFailureKind.Unknown, $"{ex.GetType().Name}: {ex.Message}"),
    };

    private static string SoftHint(BluetoothFailureKind kind) => kind switch
    {
        BluetoothFailureKind.DeviceBusy => "device refused (most likely still held by the other host)",
        BluetoothFailureKind.OperationInProgress => "a previous Bluetooth operation is still running",
        _ => kind.ToString(),
    };

    /// Right after a pair: wait for the HID input the stack starts on the pairing link. Bounded by
    /// PostPairHidWaitMs of wall-clock time AND by the equivalent poll count (each Windows poll is a
    /// ~0.4 s WMI query, so counting polls alone stretched "3 s" to ~8 s; the poll cap keeps tests
    /// with an instant SleepHook finite). False when HID doesn't come, the link closes, the run
    /// goes stale, or the backend can't tell; the caller then drops the link and reconnects.
    private async Task<bool> HidUpAfterPairAsync(BTDevice device, string addr, Run run, bool respectSelection)
    {
        var sw = Stopwatch.StartNew();
        var maxPolls = Math.Max(1, PostPairHidWaitMs / Math.Max(1, HidPollIntervalMs));
        for (var poll = 0; ; poll++)
        {
            if (StaleReason(run, respectSelection) is not null) return false;
            var ready = await SafeHidReadyAsync(device);
            if (ready is null) return false;
            if (ready == true)
            {
                Debug($"{device.Name}: HID up on the pairing link after {Ms(sw)}");
                return true;
            }
            if (poll >= maxPolls || sw.ElapsedMilliseconds >= PostPairHidWaitMs) break;
            if (!await SafeIsConnectedAsync(device))
            {
                if (poll > 0) Debug($"{device.Name}: the pairing link closed without HID ({Ms(sw)}); reconnecting");
                return false;
            }
            await SleepHook(HidPollIntervalMs);
        }
        Debug($"{device.Name}: no HID on the pairing link after {Ms(sw)}; dropping it and reconnecting");
        return false;
    }

    /// Wait until the device's HID input is up (same wall-clock + poll-count bound, using
    /// HidReadyTimeoutMs). true/false, or null when the backend can't tell (then the link alone
    /// counts). Returns early when the run goes stale; the caller checks staleness next.
    private async Task<bool?> WaitForHidAsync(BTDevice device, string addr, Run run, bool respectSelection)
    {
        var sw = Stopwatch.StartNew();
        var maxPolls = Math.Max(1, HidReadyTimeoutMs / Math.Max(1, HidPollIntervalMs));
        for (var poll = 0; ; poll++)
        {
            if (StaleReason(run, respectSelection) is not null) return false;
            var ready = await SafeHidReadyAsync(device);
            if (ready is null) return null;
            if (ready == true)
            {
                if (poll > 0) Debug($"{device.Name}: HID up after {Ms(sw)}");
                return true;
            }
            if (poll >= maxPolls || sw.ElapsedMilliseconds >= HidReadyTimeoutMs) break;
            if (!await SafeIsConnectedAsync(device))
            {
                Warn($"{device.Name}: the link dropped before HID came up ({Ms(sw)})");
                return false;
            }
            await SleepHook(HidPollIntervalMs);
        }
        Warn($"{device.Name}: link up but no HID after {Ms(sw)}");
        return false;
    }

    // ---- Unpair bookkeeping (spec §3 "pair waits for a recent unpair") ----

    /// Every engine unpair goes through here so a later pair can wait for it (in flight) and keep
    /// Config.PostUnpairSettleMs after it returned.
    private async Task UnpairTrackedAsync(BTDevice device)
    {
        var task = WithTimeoutAsync(ct => _bt.UnpairAsync(device.Address, ct));
        _unpairInFlight[device.Id] = task;
        try { await task; }
        finally
        {
            if (_unpairInFlight.TryGetValue(device.Id, out var t) && ReferenceEquals(t, task))
                _unpairInFlight.Remove(device.Id);
            _lastUnpairReturned[device.Id] = Clock();
        }
    }

    private async Task BestEffortUnpairAsync(BTDevice device, string what)
    {
        try { await UnpairTrackedAsync(device); }
        catch (Exception ex) { Warn($"{device.Name}: {what} failed (continuing): {Classify(ex).Summary}"); }
    }

    /// Wait for an unpair of this device still in flight (a superseded release), then until
    /// Config.PostUnpairSettleMs have passed since the last one returned. Returns the stale reason if the
    /// run was superseded meanwhile, else null.
    private async Task<string?> WaitForUnpairGateAsync(BTDevice device, Run run, bool respectSelection)
    {
        if (_unpairInFlight.TryGetValue(device.Id, out var inFlight))
        {
            var sw = Stopwatch.StartNew();
            Debug($"{device.Name}: waiting for an unpair of this device still in flight before pairing");
            try { await inFlight; } catch { /* its owner logs the failure */ }
            Debug($"{device.Name}: in-flight unpair finished after {Ms(sw)}");
        }
        var settleMs = Math.Max(0, Config.PostUnpairSettleMs);
        if (settleMs > 0 && _lastUnpairReturned.TryGetValue(device.Id, out var last))
        {
            var remaining = settleMs - (int)(Clock() - last).TotalMilliseconds;
            if (remaining > 0)
            {
                Debug($"{device.Name}: last unpair returned {settleMs - remaining} ms ago; settling {remaining} ms before pairing");
                await SleepHook(remaining);
            }
        }
        return StaleReason(run, respectSelection);
    }

    // ---- Release-overtakes-pair undo (spec §3: a pair that completes after a release is undone) ----

    private static void NoteIntent(Dictionary<Guid, int> map, Guid id, int token)
    {
        if (!map.TryGetValue(id, out var existing) || token > existing) map[id] = token;
    }

    /// A release (per device, or the suspend release) was requested after this run started, and
    /// no newer connect has been requested since (a KVM bounce's new connect must keep the bond).
    private bool ReleaseRequestedAfter(BTDevice device, Run run)
    {
        var release = Math.Max(
            _releaseRequestedToken.TryGetValue(device.Id, out var r) ? r : 0,
            Volatile.Read(ref _globalReleaseToken));
        var connect = _connectRequestedToken.TryGetValue(device.Id, out var c) ? c : 0;
        return release > run.Token && release > connect;
    }

    /// The run went stale after this try paired the device. If a release overtook it, the bond
    /// must not stand (the other host would be locked out): unpair it again. Otherwise a newer
    /// connect owns the device; leave it.
    private async Task AbortAfterPairAsync(BTDevice device, Run run, string reason)
    {
        if (device.ManagePairing && ReleaseRequestedAfter(device, run))
        {
            Warn($"{device.Name}: connect aborted {reason}; a release was requested meanwhile, so the new bond is undone");
            await BestEffortUnpairAsync(device, "undo a pair that completed after a release");
        }
        else
        {
            Debug($"{device.Name}: connect aborted {reason}");
        }
        MarkAborted(device.Id);
    }

    /// Run a step whose failure must not abort the flow, but must be visible in the log.
    private async Task BestEffortAsync(BTDevice device, string what, Func<CancellationToken, Task> op)
    {
        try { await WithTimeoutAsync(op); }
        catch (Exception ex) { Warn($"{device.Name}: {what} failed (continuing): {Classify(ex).Summary}"); }
    }

    private async Task<bool> SafeIsConnectedAsync(BTDevice device)
    {
        try { return await WithTimeoutAsync(ct => _bt.IsConnectedAsync(device.Address, ct)); }
        catch (Exception ex)
        {
            Warn($"{device.Name}: connection status query failed: {Classify(ex).Summary}");
            return false;
        }
    }

    /// HID readiness with the per-call timeout. A failed/hung query counts as "can't tell" (null):
    /// the link alone then decides, which is the pre-HID-check behavior.
    private async Task<bool?> SafeHidReadyAsync(BTDevice device)
    {
        try { return await WithTimeoutAsync(ct => _bt.IsHidReadyAsync(device.Address, ct)); }
        catch (Exception ex)
        {
            Warn($"{device.Name}: HID readiness query failed: {Classify(ex).Summary}");
            return null;
        }
    }

    internal TimeSpan CallTimeout =>
        CallTimeoutOverride ?? TimeSpan.FromSeconds(Math.Max(1, Config.BtCallTimeoutSecs));

    /// Run a Bluetooth call with the configured per-call timeout. Uses a cancel-after token (which
    /// WinRT AsTask honors) rather than a racing Task.Delay, so a completed call leaves no canceled
    /// task behind (avoids spurious first-chance TaskCanceledExceptions in the debugger).
    private async Task WithTimeoutAsync(Func<CancellationToken, Task> op)
    {
        var timeout = CallTimeout;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await op(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"no response within {timeout.TotalSeconds:0.#} s");
        }
    }

    /// Value-returning variant. WaitAsync makes the await itself honor the timeout even when the
    /// operation ignores its token (e.g. a hung WMI query).
    private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> op)
    {
        var timeout = CallTimeout;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await op(cts.Token).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"no response within {timeout.TotalSeconds:0.#} s");
        }
    }

    /// Fire-and-forget wrapper: background work must never fault unobserved (that would only
    /// surface, if ever, when the GC finalizes the task).
    private async Task RunLoggedAsync(Func<Task> work, string what)
    {
        try { await work(); }
        catch (Exception ex) { Log(LogLevel.Error, $"{what} failed unexpectedly", ex); }
    }

    private void MarkBusy(Guid id) => _busyDevices[id] = _busyDevices.TryGetValue(id, out var n) ? n + 1 : 1;

    private void ClearBusy(Guid id)
    {
        if (!_busyDevices.TryGetValue(id, out var n)) return;
        if (n <= 1) _busyDevices.Remove(id);
        else _busyDevices[id] = n - 1;
    }

    private bool IsBusy(Guid id) => _busyDevices.ContainsKey(id);

    private DeviceStatus? GetStatus(Guid id) => _statuses.TryGetValue(id, out var s) ? s : null;

    /// An aborted connect resets only the status it set itself (Connecting / a per-try Error).
    /// If a newer run already reported Connected/Disconnected, that result stands.
    private void MarkAborted(Guid id)
    {
        if (GetStatus(id) is { Kind: DeviceStatusKind.Connecting or DeviceStatusKind.Error })
            SetStatus(id, DeviceStatus.Idle);
    }

    private void SetStatus(Guid id, DeviceStatus status)
    {
        var had = _statuses.TryGetValue(id, out var existing);
        if (had && existing == status) return;
        _statuses[id] = status;
        RaiseChanged();
        if (status == DeviceStatus.Connected && (!had || existing != DeviceStatus.Connected)) OnBecameConnected(id);
    }

    private void RaiseChanged() => Changed?.Invoke();

    private void Log(LogLevel level, string message, Exception? ex = null) => _log.Write(level, Cat, message, ex);
    private void Debug(string message) => Log(LogLevel.Debug, message);
    private void Info(string message) => Log(LogLevel.Info, message);
    private void Warn(string message) => Log(LogLevel.Warn, message);

    private static string Ms(Stopwatch sw) =>
        sw.ElapsedMilliseconds < 10_000 ? $"{sw.ElapsedMilliseconds} ms" : $"{sw.Elapsed.TotalSeconds:0.0} s";

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
