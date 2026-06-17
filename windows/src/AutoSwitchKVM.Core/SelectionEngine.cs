using AutoSwitchKVM.Core.Models;

namespace AutoSwitchKVM.Core;

/// Core state machine: maps USB source presence to connect/disconnect of the enabled Bluetooth
/// devices. Ported 1:1 from osx/Sources/AutoSwitchKVM/Engine/SelectionEngine.swift.
///
/// Threading: like the macOS engine (which is @MainActor), this assumes single-threaded use - the
/// app marshals calls onto the UI thread (DispatcherQueue). It does not lock internally.
///
/// Determinism for tests: the production debounce/poll timers are separate from the transition
/// logic. Tests drive HandleUsb/ApplyUsbSnapshot + EvaluateNowAsync directly (no timers), and set
/// SleepHook to a no-op so per-device delay, post-pair settle, and retry backoff are instant.
public sealed class SelectionEngine
{
    private readonly IUsbMonitor _usb;
    private readonly IBluetoothController _bt;
    private readonly Random _rng = new();

    private readonly HashSet<ushort> _presentProductIDs = new();
    /// Presence the automation last acted on (a latch, distinct from Selected which tracks raw
    /// presence even while paused).
    private bool _automationActed;
    private int _runToken;                       // bumps each evaluation to cancel in-flight work
    private readonly HashSet<Guid> _busyDevices = new();
    private readonly Dictionary<Guid, DeviceStatus> _statuses = new();

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

    public SelectionEngine(AppConfig config, IUsbMonitor usb, IBluetoothController bt)
    {
        Config = config;
        _usb = usb;
        _bt = bt;
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
        if (_busyDevices.Contains(device.Id)) return;
        UpdateStatus(device, change.IsConnected);
    }

    /// Poll live connection state for all devices (safety net for a missed event). Refreshes adapter
    /// power first; skips devices that are mid-transition.
    public async Task PollStatusesAsync()
    {
        await RefreshPowerAsync();
        foreach (var device in Config.Devices)
        {
            if (_busyDevices.Contains(device.Id)) continue;
            if (!BluetoothPowered)
            {
                SetStatus(device.Id, DeviceStatus.BluetoothOff);
                continue;
            }
            var connected = await _bt.IsConnectedAsync(device.Address);
            UpdateStatus(device, connected);
        }
    }

    /// Reflect a device's live connection state, and flag a passive drop (connected -> disconnected
    /// while the source is present, BT is on, and we're not mid-transition) as unexpected.
    private void UpdateStatus(BTDevice device, bool connected)
    {
        DeviceStatus? old = _statuses.TryGetValue(device.Id, out var o) ? o : null;
        var @new = connected ? DeviceStatus.Connected : DeviceStatus.Disconnected;
        if (old != @new) SetStatus(device.Id, @new);

        if (!connected && old == DeviceStatus.Connected && Selected && BluetoothPowered && device.Enabled)
        {
            OnUnexpectedDisconnect?.Invoke(device);
        }
    }

    /// Refresh adapter power; on a false->true transition, re-seed so devices reconnect.
    public async Task RefreshPowerAsync()
    {
        var powered = await _bt.IsPoweredOnAsync() ?? true;
        if (powered == BluetoothPowered) return;
        BluetoothPowered = powered;
        RaiseChanged();
        if (powered) await SeedAsync();
    }

    // MARK: - Seeding & USB input

    /// Establish presence from scratch (launch, wake, profile switch) and evaluate.
    public async Task SeedAsync()
    {
        _presentProductIDs.Clear();
        _automationActed = false;
        var source = Config.Source;
        if (source is not null)
        {
            foreach (var d in _usb.Snapshot())
                if (d.VendorID == source.VendorID && source.ProductIDs.Contains(d.ProductID))
                    _presentProductIDs.Add(d.ProductID);
        }
        LastReason = "initial scan";
        await EvaluateNowAsync();
    }

    public Task ReevaluateAsync() => SeedAsync();

    /// Recompute presence from a full USB snapshot (the Windows reconcile path). Pure: call
    /// EvaluateNowAsync (or OnUsbSnapshotAsync) to act on it.
    public void ApplyUsbSnapshot(IReadOnlyList<UsbDeviceInfo> snapshot)
    {
        _presentProductIDs.Clear();
        var source = Config.Source;
        if (source is not null)
        {
            foreach (var d in snapshot)
                if (d.VendorID == source.VendorID && source.ProductIDs.Contains(d.ProductID))
                    _presentProductIDs.Add(d.ProductID);
        }
        LastReason = "usb snapshot";
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
        _ = DebouncedEvaluateAsync(ms, cts.Token);
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
        RaiseChanged();

        if (Config.Paused) return;          // automation suspended; don't act

        if (present && !_automationActed)
        {
            _automationActed = true;
            _runToken++;
            await ConnectAllAsync(_runToken);
        }
        else if (!present && _automationActed)
        {
            _automationActed = false;
            _runToken++;
            await DisconnectAllAsync();
        }
    }

    // MARK: - Manual quick actions (ignore selection, operate on all configured devices)

    public async Task ConnectAllNowAsync()
    {
        _runToken++;
        var token = _runToken;
        foreach (var device in Config.Devices)
        {
            _busyDevices.Add(device.Id);
            await ConnectOneAsync(device, token, respectSelection: false);
            _busyDevices.Remove(device.Id);
        }
    }

    public async Task DisconnectAllNowAsync()
    {
        foreach (var device in Config.Devices)
        {
            _busyDevices.Add(device.Id);
            await DisconnectOneAsync(device);
            _busyDevices.Remove(device.Id);
        }
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

    /// Backoff in milliseconds with +/-15% jitter (used between connect retries).
    internal int BackoffMillisJittered(int @base, int attempt)
    {
        var jittered = BackoffSeconds(@base, attempt) * (0.85 + _rng.NextDouble() * 0.30);
        return (int)Math.Max(0, jittered * 1000.0);
    }

    // MARK: - Actions

    private async Task ConnectAllAsync(int token)
    {
        foreach (var device in Config.Devices)
        {
            if (!device.Enabled) continue;
            if (token != _runToken) return;
            _busyDevices.Add(device.Id);
            await ConnectOneAsync(device, token, respectSelection: true);
            _busyDevices.Remove(device.Id);
        }
    }

    private async Task ConnectOneAsync(BTDevice device, int token, bool respectSelection)
    {
        var cfg = Config;
        var addr = device.Address;
        SetStatus(device.Id, DeviceStatus.Connecting);

        // Optional per-device stagger before connecting.
        if (device.ConnectDelayMs > 0)
        {
            await SleepHook(device.ConnectDelayMs);
            if (token != _runToken || (respectSelection && !Selected))
            {
                SetStatus(device.Id, DeviceStatus.Idle);
                return;
            }
        }

        var attempt = 0;
        while (attempt < cfg.ConnectRetryMax)
        {
            if (!BluetoothPowered) { SetStatus(device.Id, DeviceStatus.BluetoothOff); return; }
            var aborted = token != _runToken || (respectSelection && !Selected);
            if (aborted) { SetStatus(device.Id, DeviceStatus.Idle); return; }
            attempt++;

            if (!device.ManagePairing && await _bt.IsConnectedAsync(addr))
            {
                SetStatus(device.Id, DeviceStatus.Connected);
                return;
            }

            // A clean connect = ConnectAsync returned without throwing. That's the reliable "device
            // is up" signal; IsConnected alone can be a link-only state that doesn't actually work.
            var connectedCleanly = false;
            try
            {
                if (device.ManagePairing)
                {
                    await WithTimeoutAsync(ct => _bt.PairAsync(addr, ct));
                    // The connection reported right after pairing is unreliable; drop and reconnect.
                    try { await WithTimeoutAsync(ct => _bt.DisconnectAsync(addr, ct)); } catch { }
                    await SleepHook(PostPairSettleMs);
                }
                await WithTimeoutAsync(ct => _bt.ConnectAsync(addr, ct));
                connectedCleanly = true;
            }
            catch (Exception ex)
            {
                SetStatus(device.Id, DeviceStatus.Error(ex.Message));
            }

            if (connectedCleanly && await _bt.IsConnectedAsync(addr))
            {
                SetStatus(device.Id, DeviceStatus.Connected);
                OnNotice?.Invoke("Connected", device.Name);
                return;
            }
            else
            {
                // Don't trust a link-only state; drop any partial connection so the next try is clean.
                try { await WithTimeoutAsync(ct => _bt.DisconnectAsync(addr, ct)); } catch { }
            }

            if (attempt < cfg.ConnectRetryMax)
                await SleepHook(BackoffMillisJittered(cfg.ConnectRetrySecs, attempt));
        }

        if (GetStatus(device.Id) != DeviceStatus.Connected)
        {
            SetStatus(device.Id, DeviceStatus.Error($"gave up after {cfg.ConnectRetryMax} attempts"));
            OnNotice?.Invoke("Connection failed", $"{device.Name}: gave up after {cfg.ConnectRetryMax} attempts");
        }
    }

    private async Task DisconnectAllAsync()
    {
        foreach (var device in Config.Devices)
        {
            if (!device.Enabled) continue;
            _busyDevices.Add(device.Id);
            await DisconnectOneAsync(device);
            _busyDevices.Remove(device.Id);
        }
    }

    private async Task DisconnectOneAsync(BTDevice device)
    {
        var addr = device.Address;
        try
        {
            await WithTimeoutAsync(ct => _bt.DisconnectAsync(addr, ct));
            if (device.ManagePairing)
                await WithTimeoutAsync(ct => _bt.UnpairAsync(addr, ct));
            SetStatus(device.Id, DeviceStatus.Disconnected);
            OnNotice?.Invoke("Disconnected", device.Name);
        }
        catch (Exception ex)
        {
            SetStatus(device.Id, DeviceStatus.Error(ex.Message));
        }
    }

    // MARK: - Helpers

    /// Run a Bluetooth call with the configured per-call timeout.
    private async Task WithTimeoutAsync(Func<CancellationToken, Task> op)
    {
        var secs = Math.Max(1, Config.BtCallTimeoutSecs);
        using var cts = new CancellationTokenSource();
        var opTask = op(cts.Token);
        var delayTask = Task.Delay(TimeSpan.FromSeconds(secs), cts.Token);
        var winner = await Task.WhenAny(opTask, delayTask);
        cts.Cancel();
        if (winner == delayTask && !opTask.IsCompleted)
            throw new TimeoutException($"Bluetooth call timed out after {secs}s");
        await opTask; // observe exceptions / completion
    }

    private DeviceStatus? GetStatus(Guid id) => _statuses.TryGetValue(id, out var s) ? s : null;

    private void SetStatus(Guid id, DeviceStatus status)
    {
        if (_statuses.TryGetValue(id, out var existing) && existing == status) return;
        _statuses[id] = status;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();
}
