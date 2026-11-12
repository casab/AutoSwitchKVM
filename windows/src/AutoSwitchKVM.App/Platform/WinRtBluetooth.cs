using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using AutoSwitchKVM.App.Support;
using AutoSwitchKVM.Core;
using Microsoft.UI.Dispatching;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation;

namespace AutoSwitchKVM.App.Platform;

/// IBluetoothController backed by native WinRT. Implements the call sequences validated against the
/// real Magic Trackpad in Milestone 0 (windows/PLAN.md "spike findings", windows/spikes/Spike1-2).
///
/// Classic-HID mapping (the bond is exclusive; there is no disconnect-without-unpair):
///   ConnectAsync    = ensure PAIRED (pairing brings the HID up). No-op if already paired.
///   DisconnectAsync = no-op. Windows cannot drop a Classic-HID link while keeping the bond; the
///                     handoff release is UnpairAsync. (This makes the shared engine flow correct:
///                     its mid-connect "disconnect" becomes a no-op, and disconnect-on-leave is the
///                     no-op followed by UnpairAsync.)
///   PairAsync       = resolve the endpoint by address + Custom.PairAsync(ConfirmOnly) auto-accept.
///                     Failures are classified (BluetoothFailureKind) for the engine's retry policy;
///                     a ceremony that outlives its timeout is tracked so retries wait for it.
///   UnpairAsync     = BluetoothRemoveDevice first (tears down the active link + bond), WinRT UnpairAsync fallback.
///   IsConnected     = ConnectionStatus == Connected (primary), corroborated by a BTHENUM HID node.
///   IsPoweredOn     = Bluetooth Radio.State == On.
///
/// A CLI fallback (PolarGoose) remains a documented option if a future device can't be driven by
/// WinRT custom pairing.
public sealed class WinRtBluetooth : IBluetoothController
{
    private readonly DispatcherQueue? _dispatcher;
    private readonly object _gate = new();
    private readonly List<(BluetoothDevice dev, TypedEventHandler<BluetoothDevice, object> handler)> _subs = new();
    // Serializes pair/unpair so overlapping radio operations (engine retries + manual test buttons)
    // can't run concurrent inquiries against the one radio (the cause of AuthenticationTimeout churn).
    private readonly SemaphoreSlim _opLock = new(1, 1);

    /// Pass the UI DispatcherQueue so connection-change callbacks reach the engine on the UI thread.
    public WinRtBluetooth(DispatcherQueue? dispatcher = null)
    {
        _dispatcher = dispatcher;
    }

    // ---- Power ----

    public async Task<bool?> IsPoweredOnAsync(CancellationToken ct = default)
    {
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask(ct);
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (bt is null) { Log.Warn("bt", "no Bluetooth radio found"); return null; }
            return bt.State == RadioState.On;
        }
        catch (Exception ex)
        {
            Log.Error("bt", "radio state query failed", ex);
            return null; // access not granted / no radio -> unknown
        }
    }

    // ---- Connection state ----

    public async Task<bool> IsConnectedAsync(string address, CancellationToken ct = default)
    {
        try
        {
            using var dev = await BluetoothDevice.FromBluetoothAddressAsync(ParseAddress(address)).AsTask(ct);
            // WinRT answered: trust it. An unknown device (null) is certainly not connected to
            // us. Falling through to the HID node on a mere "not connected" answer would let a
            // stale PnP entry masquerade as a working connection.
            if (dev is not null)
                return dev.ConnectionStatus == BluetoothConnectionStatus.Connected;
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BluetoothException)
        {
            throw;  // invalid address: not a WinRT hiccup, don't mask it with the HID fallback
        }
        catch (Exception ex)
        {
            // WinRT itself failed — corroborate via the BTHENUM HID node, off-thread (a
            // synchronous WMI query on the UI thread would jank every poll).
            Log.Warn("bt", $"isConnected({address}) WinRT query failed ({ex.Message}); using HID node fallback");
            var digits = Digits(address);
            return await Task.Run(() => HidNodePresent(digits), ct);
        }
    }

    // ---- Connect / disconnect (Classic-HID semantics; see class doc) ----

    // Connect == ensure paired (PairAsync is idempotent: no-op if already paired).
    public Task ConnectAsync(string address, CancellationToken ct = default) => PairAsync(address, ct);

    public Task DisconnectAsync(string address, CancellationToken ct = default) => Task.CompletedTask;

    // ---- Pair / unpair ----

    // WinRT pairing ignores cancellation: once Custom.PairAsync starts, the ceremony runs inside
    // Windows until it completes or hits its own ~45-60 s timeout, whatever the caller's token
    // says. Before this was tracked, a timed-out pair released _opLock while Windows was still
    // pairing, and every retry got an instant OperationAlreadyInProgress until the engine gave up
    // (64 of them in the 2026-10-21 log). Now a timed-out ceremony is kept as an "orphan": while it
    // runs, a new pair fails fast with OperationInProgress (the engine soft-retries without
    // spending an attempt), and a watcher logs how it ended and undoes a late success if a
    // release was requested in the meantime.
    // Observed on hardware (2026-11-12): an orphan ends ~50-55 s after it started, as Canceled
    // (op.Cancel() only takes effect at Windows' own pairing timeout).
    private Task<DevicePairingResult>? _orphanPair;
    private string? _orphanAddress;
    private DateTime _orphanSince;
    /// Latest intent per device (digits -> want paired + sequence). Decides whether a pairing that
    /// completes after its caller gave up is kept or removed again, and lets a queued pair notice
    /// that a release was requested after it.
    private readonly Dictionary<string, (bool Want, long Seq)> _wantPaired = new();
    private long _intentSeq;

    // The trackpad uses ConfirmOnly; accept the common HID ceremonies.
    private const DevicePairingKinds AcceptedKinds = DevicePairingKinds.ConfirmOnly
        | DevicePairingKinds.ProvidePin
        | DevicePairingKinds.ConfirmPinMatch
        | DevicePairingKinds.DisplayPin;

    public async Task PairAsync(string address, CancellationToken ct = default)
    {
        var addr = ParseAddress(address);
        var digits = Digits(address);
        var mySeq = SetIntent(digits, wantPaired: true);

        await _opLock.WaitAsync(ct);
        try
        {
            // An orphaned ceremony is still running: fail fast as OperationInProgress (the engine
            // soft-retries in ~3 s without spending an attempt). Waiting inside this call instead
            // ate the caller's timeout — on hardware a retry waited 19 s for the orphan and then
            // had only 13 s left to pair.
            string? orphanInfo = null;
            lock (_gate)
            {
                if (_orphanPair is { IsCompleted: false })
                    orphanInfo = $"{_orphanAddress}, started {(DateTime.UtcNow - _orphanSince).TotalSeconds:0} s ago";
            }
            if (orphanInfo is not null)
            {
                Log.Info("bt", $"pair {address}: a previous pairing is still running in Windows ({orphanInfo}); not starting another");
                throw new BluetoothException(BluetoothFailureKind.OperationInProgress,
                    $"a previous pairing is still running in Windows ({orphanInfo})", "orphaned PairAsync");
            }

            // A release requested after this pair was (e.g. the source left while we queued for
            // the radio) wins: pairing now would bond the device to this PC after the handoff.
            if (ReleaseRequestedSince(digits, mySeq))
            {
                Log.Info("bt", $"pair {address}: skipped - a release was requested after this pair was queued");
                throw new BluetoothException(BluetoothFailureKind.Unknown,
                    "pair skipped: superseded by a later release request", "superseded");
            }
            await PairCoreAsync(address, addr, ct);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// Called with _opLock held.
    private async Task PairCoreAsync(string address, ulong addr, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // FromBluetoothAddressAsync resolves the pairable AssociationEndpoint INSTANTLY (~10ms) -
        // the exact same endpoint a 30s inquiry would "discover" (verified: identical Id, CanPair=
        // True). So we pair it directly; no DeviceWatcher / FindAllAsync inquiry needed.
        var dev = await BluetoothDevice.FromBluetoothAddressAsync(addr).AsTask(ct);
        if (dev is null)
        {
            throw new BluetoothException(BluetoothFailureKind.DeviceNotFound,
                $"{address} is not known to Windows (turn it on / bring it in range)", "FromBluetoothAddressAsync=null");
        }

        DeviceInformationCustomPairing? custom = null;
        TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs>? handler = null;
        var handedOff = false;   // ownership of dev + handler passed to the orphan watcher
        try
        {
            var pairing = dev.DeviceInformation.Pairing;
            Log.Info("bt", $"pair {address}: resolved '{dev.Name}' conn={dev.ConnectionStatus} " +
                $"IsPaired={pairing.IsPaired} CanPair={pairing.CanPair} ({sw.ElapsedMilliseconds} ms)");

            if (pairing.IsPaired)
            {
                Log.Info("bt", $"pair {address}: already paired");
                return;
            }

            custom = pairing.Custom;
            var requested = 0;   // did the device ever start the ceremony? (tells "no answer" apart)

            // Auto-accept the ceremony (a real .NET delegate; no PowerShell-style deadlock).
            handler = (_, e) =>
            {
                Interlocked.Exchange(ref requested, 1);
                Log.Info("bt", $"pair {address}: PairingRequested kind={e.PairingKind} after {sw.ElapsedMilliseconds} ms");
                if (e.PairingKind == DevicePairingKinds.ProvidePin) e.Accept("0000");
                else e.Accept();
            };
            custom.PairingRequested += handler;

            // Do NOT gate on CanPair. No token on AsTask: cancellation is handled explicitly below,
            // because cancelling the WinRT operation does not actually stop the ceremony.
            var op = custom.PairAsync(AcceptedKinds);
            var task = op.AsTask();

            var cancelledByUs = false;
            if (!await CompletesWithinAsync(task, ct))
            {
                cancelledByUs = true;
                try { op.Cancel(); } catch { /* best-effort; not reliably honored for pairing */ }
                if (!await CompletesWithinAsync(task, TimeSpan.FromSeconds(2)))
                {
                    // Still running inside Windows. Hand it to a watcher (keep the handler: the
                    // device may still ask) and make the next pair wait for it.
                    handedOff = true;
                    lock (_gate) { _orphanPair = task; _orphanAddress = address; _orphanSince = DateTime.UtcNow - sw.Elapsed; }
                    _ = WatchOrphanAsync(task, address, dev, custom, handler, sw);
                    var why = Volatile.Read(ref requested) == 1
                        ? "the device started pairing but did not finish"
                        : "the device never answered (no pairing request)";
                    Log.Warn("bt", $"pair {address}: stopped waiting after {sw.ElapsedMilliseconds} ms - {why}; " +
                        "Windows is still running the ceremony in the background");
                    throw new BluetoothException(BluetoothFailureKind.Timeout,
                        $"{why} within {sw.Elapsed.TotalSeconds:0} s", "PairAsync still pending");
                }
            }

            DevicePairingResult result;
            try { result = await task; }
            catch (Exception ex)
            {
                throw new BluetoothException(BluetoothFailureKind.Unknown,
                    $"pairing call failed: {ex.Message}", ex.GetType().Name, ex);
            }

            var status = result.Status;
            Log.Info("bt", $"pair {address}: result={status} after {sw.ElapsedMilliseconds} ms" +
                (cancelledByUs ? " (after we cancelled it)" : ""));
            if (status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)
            {
                // Pairing created a fresh device record — the old ConnectionStatusChanged
                // subscription (if any) points at a dead object.
                RearmMonitoring(address);
                return;
            }
            if (cancelledByUs)
            {
                throw new BluetoothException(BluetoothFailureKind.Timeout,
                    $"no pairing response within {sw.Elapsed.TotalSeconds:0} s (cancelled: {status})", status.ToString());
            }
            throw new BluetoothException(KindFor(status), $"pairing refused: {status}", status.ToString());
        }
        finally
        {
            if (!handedOff)
            {
                if (custom is not null && handler is not null)
                {
                    try { custom.PairingRequested -= handler; } catch { /* ignore */ }
                }
                dev.Dispose();
            }
        }
    }

    /// A pair that timed out but is still running inside Windows: log how it ends, and if it ends
    /// Paired after a release was requested, remove the bond again so the other host can have it.
    private async Task WatchOrphanAsync(
        Task<DevicePairingResult> task, string address, BluetoothDevice dev,
        DeviceInformationCustomPairing custom,
        TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs> handler,
        Stopwatch sw)
    {
        try
        {
            DevicePairingResultStatus? status = null;
            try { status = (await task).Status; }
            catch (Exception ex) { Log.Warn("bt", $"pair {address}: background pairing faulted: {ex.Message}"); }

            Log.Info("bt", $"pair {address}: background pairing finished after {sw.Elapsed.TotalSeconds:0.0} s: " +
                (status?.ToString() ?? "error"));
            if (status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)) return;

            await _opLock.WaitAsync();
            try
            {
                if (WantsPaired(Digits(address)))
                {
                    Log.Info("bt", $"pair {address}: late pairing kept (a connect is still wanted)");
                    RearmMonitoring(address);
                }
                else
                {
                    Log.Warn("bt", $"pair {address}: late pairing completed after a release was requested - removing it again");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await UnpairCoreAsync(address, cts.Token);
                }
            }
            finally
            {
                _opLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error("bt", $"pair {address}: background pairing follow-up failed", ex);
        }
        finally
        {
            try { custom.PairingRequested -= handler; } catch { /* ignore */ }
            try { dev.Dispose(); } catch { /* ignore */ }
            lock (_gate)
            {
                if (ReferenceEquals(_orphanPair, task)) { _orphanPair = null; _orphanAddress = null; }
            }
        }
    }

    public async Task UnpairAsync(string address, CancellationToken ct = default)
    {
        ParseAddress(address);   // validate before touching state
        SetIntent(Digits(address), wantPaired: false);

        await _opLock.WaitAsync(ct);
        try
        {
            await UnpairCoreAsync(address, ct);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// Called with _opLock held.
    private async Task UnpairCoreAsync(string address, CancellationToken ct)
    {
        var addr = ParseAddress(address);
        var sw = Stopwatch.StartNew();

        // Forcefully remove the device via the classic Win32 API (the same call behind Control
        // Panel "Remove device"). Unlike WinRT UnpairAsync - which removes the pairing record on
        // the Windows side - this also tears down the active ACL link (LMP detach), so the
        // trackpad actually lets go and the other host (Mac) can take it. WinRT unpair alone was
        // leaving the device attached on the radio.
        // Synchronous and slow (~3.4 s on hardware: it waits for the link teardown). Run it off
        // the calling thread — the engine calls from the UI thread, which would freeze the tray
        // and keep the engine's per-call timeout from firing.
        uint? rc = null;
        try
        {
            rc = await Task.Run(() => RemoveDevice(addr), ct);
        }
        catch (Exception ex)
        {
            Log.Warn("bt", $"unpair {address}: BluetoothRemoveDevice threw: {ex.Message}; trying WinRT unpair");
        }

        if (rc == 0)
        {
            Log.Info("bt", $"unpair {address}: BluetoothRemoveDevice OK - link dropped and bond removed ({sw.ElapsedMilliseconds} ms)");
            RearmMonitoring(address);
            return;
        }
        if (rc == ErrorNotFound)
            Log.Info("bt", $"unpair {address}: not in Windows' paired list (ERROR_NOT_FOUND); confirming via WinRT");
        else if (rc is not null)
            Log.Warn("bt", $"unpair {address}: BluetoothRemoveDevice failed: {Win32Name(rc.Value)}; trying WinRT unpair");

        // Fallback: if the forceful remove didn't take, fall back to WinRT unpair.
        using (var dev = await BluetoothDevice.FromBluetoothAddressAsync(addr).AsTask(ct))
        {
            if (dev?.DeviceInformation.Pairing.IsPaired == true)
            {
                var result = await dev.DeviceInformation.Pairing.UnpairAsync().AsTask(ct);
                Log.Info("bt", $"unpair {address}: WinRT result={result.Status} ({sw.ElapsedMilliseconds} ms)");
                if (result.Status != DeviceUnpairingResultStatus.Unpaired &&
                    result.Status != DeviceUnpairingResultStatus.AlreadyUnpaired)
                {
                    var kind = result.Status == DeviceUnpairingResultStatus.OperationAlreadyInProgress
                        ? BluetoothFailureKind.OperationInProgress
                        : BluetoothFailureKind.Unknown;
                    throw new BluetoothException(kind,
                        $"unpair failed: {result.Status} (BluetoothRemoveDevice: {(rc is null ? "threw" : Win32Name(rc.Value))})",
                        result.Status.ToString());
                }
            }
            else
            {
                Log.Info("bt", $"unpair {address}: already unpaired ({sw.ElapsedMilliseconds} ms)");
            }
        }

        // The unpair destroyed the device record; drop (and later re-create) its subscription.
        RearmMonitoring(address);
    }

    /// Map a WinRT pairing result to the engine's retry policy (see BluetoothFailureKind).
    internal static BluetoothFailureKind KindFor(DevicePairingResultStatus status) => status switch
    {
        // The device answered but won't pair right now — for an exclusive-bond device, the other
        // host still holds it. Seen on hardware right after a KVM switch, clearing within ~20 s.
        DevicePairingResultStatus.AuthenticationNotAllowed
            or DevicePairingResultStatus.ConnectionRejected
            or DevicePairingResultStatus.TooManyConnections
            or DevicePairingResultStatus.NotReadyToPair
            or DevicePairingResultStatus.RemoteDeviceHasAssociation => BluetoothFailureKind.DeviceBusy,
        DevicePairingResultStatus.OperationAlreadyInProgress => BluetoothFailureKind.OperationInProgress,
        DevicePairingResultStatus.AuthenticationTimeout => BluetoothFailureKind.Timeout,
        DevicePairingResultStatus.AuthenticationFailure
            or DevicePairingResultStatus.ProtectionLevelCouldNotBeMet
            or DevicePairingResultStatus.InvalidCeremonyData => BluetoothFailureKind.AuthenticationFailed,
        DevicePairingResultStatus.HardwareFailure => BluetoothFailureKind.AdapterUnavailable,
        _ => BluetoothFailureKind.Unknown,
    };

    /// Record the latest pair/release intent for a device; returns its sequence number.
    private long SetIntent(string digits, bool wantPaired)
    {
        lock (_gate)
        {
            var seq = ++_intentSeq;
            _wantPaired[digits] = (wantPaired, seq);
            return seq;
        }
    }

    private bool WantsPaired(string digits)
    {
        lock (_gate) return !_wantPaired.TryGetValue(digits, out var i) || i.Want;
    }

    private bool ReleaseRequestedSince(string digits, long seq)
    {
        lock (_gate) return _wantPaired.TryGetValue(digits, out var i) && !i.Want && i.Seq > seq;
    }

    /// True if the task completes before the token fires (the task itself is never cancelled).
    private static async Task<bool> CompletesWithinAsync(Task task, CancellationToken ct)
    {
        if (task.IsCompleted) return true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var winner = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
        cts.Cancel();   // release the delay
        return winner == task;
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await CompletesWithinAsync(task, cts.Token);
    }

    private const uint ErrorNotFound = 0x490;

    private static string Win32Name(uint rc) => rc switch
    {
        0 => "OK",
        0x5 => "ERROR_ACCESS_DENIED",
        0x15 => "ERROR_NOT_READY",
        0x57 => "ERROR_INVALID_PARAMETER",
        0x48F => "ERROR_DEVICE_NOT_CONNECTED",
        ErrorNotFound => "ERROR_NOT_FOUND",
        _ => $"0x{rc:X}",
    };

    // ---- Paired list (for the add-device picker) ----

    public async Task<IReadOnlyList<PairedDeviceInfo>> PairedDevicesAsync(CancellationToken ct = default)
    {
        var list = new List<PairedDeviceInfo>();
        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var found = await DeviceInformation.FindAllAsync(selector).AsTask(ct);
            foreach (var di in found)
            {
                try
                {
                    using var dev = await BluetoothDevice.FromIdAsync(di.Id).AsTask(ct);
                    if (dev is null) continue;
                    var name = string.IsNullOrEmpty(dev.Name) ? di.Name : dev.Name;
                    list.Add(new PairedDeviceInfo(name, FormatMac(dev.BluetoothAddress)));
                }
                catch (Exception ex)
                {
                    Log.Warn("bt", $"paired list: skipping '{di.Name}' ({ex.Message})");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("bt", $"paired list: enumeration failed ({ex.Message})");
        }
        return list;
    }

    // ---- Diagnostics (Settings "Diagnose" button): dump everything WinRT sees for this MAC ----

    public async Task DiagnoseAsync(string address)
    {
        // Serialize with pair/unpair: the unpaired-state selector below forces a ~30 s radio
        // inquiry, and concurrent radio operations are exactly what _opLock exists to prevent.
        await _opLock.WaitAsync();
        try
        {
            await DiagnoseCoreAsync(address);
        }
        finally
        {
            _opLock.Release();
        }
    }

    private async Task DiagnoseCoreAsync(string address)
    {
        var digits = Digits(address);
        Log.Info("diag", $"===== Diagnose {address} (match digits: {digits}) =====");

        try
        {
            var radios = await Radio.GetRadiosAsync();
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            Log.Info("diag", $"radio: {(bt is null ? "<none>" : bt.State.ToString())}");
        }
        catch (Exception ex) { Log.Error("diag", "radio query failed", ex); }

        lock (_gate)
        {
            if (_orphanPair is { IsCompleted: false })
                Log.Info("diag", $"a background pairing of {_orphanAddress} is still running");
        }

        try
        {
            var addr = ParseAddress(address);
            Log.Info("diag", $"parsed address = 0x{addr:X12}");
            using var dev = await BluetoothDevice.FromBluetoothAddressAsync(addr);
            if (dev is null)
            {
                Log.Info("diag", "FromBluetoothAddressAsync: returned NULL (device not known to the system by address)");
            }
            else
            {
                var p = dev.DeviceInformation.Pairing;
                Log.Info("diag", $"FromBluetoothAddressAsync: name='{dev.Name}' conn={dev.ConnectionStatus} " +
                    $"IsPaired={p.IsPaired} CanPair={p.CanPair} kind={dev.DeviceInformation.Kind} id={dev.DeviceInformation.Id}");
            }
        }
        catch (Exception ex) { Log.Error("diag", "FromBluetoothAddressAsync failed", ex); }

        await DumpSelectorAsync("paired", BluetoothDevice.GetDeviceSelectorFromPairingState(true), digits);
        await DumpSelectorAsync("unpaired", BluetoothDevice.GetDeviceSelectorFromPairingState(false), digits);
        try { await DumpSelectorAsync("bt-classic-any", BluetoothDevice.GetDeviceSelector(), digits); }
        catch (Exception ex) { Log.Warn("diag", $"bt-classic-any selector failed: {ex.Message}"); }

        Log.Info("diag", $"BTHENUM HID node present for MAC: {HidNodePresent(digits)}");
        Log.Info("diag", "===== end Diagnose =====");
    }

    private static async Task DumpSelectorAsync(string label, string selector, string digits)
    {
        try
        {
            var found = await DeviceInformation.FindAllAsync(selector);
            Log.Info("diag", $"[{label}] {found.Count} device(s):");
            foreach (var d in found)
            {
                var hit = StripSeparators(d.Id).Contains(digits, StringComparison.OrdinalIgnoreCase) ? "  <== TARGET" : "";
                Log.Info("diag", $"  [{label}] name='{d.Name}' id={d.Id}{hit}");
            }
        }
        catch (Exception ex) { Log.Error("diag", $"[{label}] enumerate failed", ex); }
    }

    // ---- Monitoring (best-effort; the engine also polls as a safety-net) ----

    private CancellationTokenSource? _monitorCts;
    private List<string> _monitoredAddresses = new();
    private Action<ConnectionChange>? _monitorCallback;

    public void StartMonitoring(IEnumerable<string> addresses, Action<ConnectionChange> onChange)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            // Cancel any in-flight setup from a previous call — otherwise its stragglers
            // repopulate _subs after StopMonitoring cleared them.
            _monitorCts?.Cancel();
            _monitorCts = cts = new CancellationTokenSource();
            _monitoredAddresses = addresses.ToList();
            _monitorCallback = onChange;
        }
        RemoveAllSubscriptions();
        _ = SetupMonitoringAsync(_monitoredAddresses, onChange, cts.Token);
    }

    private async Task SetupMonitoringAsync(
        List<string> addresses, Action<ConnectionChange> onChange, CancellationToken ct)
    {
        foreach (var address in addresses)
        {
            if (ct.IsCancellationRequested) return;
            await SubscribeAsync(address, onChange, ct);
        }
    }

    private async Task SubscribeAsync(string address, Action<ConnectionChange> onChange, CancellationToken ct)
    {
        try
        {
            var digits = Digits(address);
            var dev = await BluetoothDevice.FromBluetoothAddressAsync(ParseAddress(address)).AsTask(ct);
            if (dev is null)
            {
                Log.Info("bt", $"monitor {address}: not known to Windows yet (unpaired); events resume after the next pair");
                return;
            }

            TypedEventHandler<BluetoothDevice, object> handler = (sender, _) =>
            {
                // Read the status HERE, not inside the dispatched lambda: StopMonitoring
                // disposes the BluetoothDevice, and a queued lambda touching a disposed
                // sender throws ObjectDisposedException on the UI thread.
                bool isConnected;
                try { isConnected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected; }
                catch { return; }  // device already disposed/removed mid-event
                Post(() => onChange(new ConnectionChange(digits, isConnected)));
            };

            dev.ConnectionStatusChanged += handler;
            lock (_gate)
            {
                if (ct.IsCancellationRequested)
                {
                    try { dev.ConnectionStatusChanged -= handler; dev.Dispose(); } catch { /* ignore */ }
                    return;
                }
                _subs.Add((dev, handler));
            }
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            // Not fatal (the engine's 10 s poll still tracks status), but event-driven status is gone.
            Log.Warn("bt", $"monitor {address}: could not subscribe to connection events ({ex.Message}); relying on the status poll");
        }
    }

    /// Unpair destroys the system's device record, killing its ConnectionStatusChanged
    /// subscription; pairing creates a fresh record. Re-arm the monitored address so the engine
    /// keeps getting events instead of degrading to the 10 s poll.
    private void RearmMonitoring(string address)
    {
        Action<ConnectionChange>? onChange;
        CancellationToken ct;
        var digits = Digits(address);
        lock (_gate)
        {
            if (_monitorCts is null || _monitorCallback is null) return;
            if (!_monitoredAddresses.Any(a => Digits(a) == digits)) return;
            onChange = _monitorCallback;
            ct = _monitorCts.Token;

            for (var i = _subs.Count - 1; i >= 0; i--)
            {
                var (dev, handler) = _subs[i];
                string subDigits;
                try { subDigits = dev.BluetoothAddress.ToString("X12"); }
                catch { subDigits = ""; }
                if (subDigits != digits) continue;
                try { dev.ConnectionStatusChanged -= handler; dev.Dispose(); } catch { /* ignore */ }
                _subs.RemoveAt(i);
            }
        }
        _ = SubscribeAsync(address, onChange, ct);
    }

    public void StopMonitoring()
    {
        lock (_gate)
        {
            _monitorCts?.Cancel();
            _monitorCts = null;
            _monitorCallback = null;
            _monitoredAddresses = new List<string>();
        }
        RemoveAllSubscriptions();
    }

    private void RemoveAllSubscriptions()
    {
        lock (_gate)
        {
            foreach (var (dev, handler) in _subs)
            {
                try { dev.ConnectionStatusChanged -= handler; dev.Dispose(); } catch { /* ignore */ }
            }
            _subs.Clear();
        }
    }

    // ---- Helpers ----

    private void Post(Action action)
    {
        if (_dispatcher != null) _dispatcher.TryEnqueue(() => action());
        else action();
    }

    /// MAC string ("3C:50:02:BF:22:45" or "3c-50-...") -> 64-bit Bluetooth address for the WinRT API.
    /// Validates the shape: a short or malformed string would otherwise silently parse into a
    /// wrong address (or throw an untyped FormatException deep inside a Bluetooth call).
    internal static ulong ParseAddress(string mac)
    {
        var digits = Digits(mac);
        if (digits.Length != 12 || !digits.All(Uri.IsHexDigit))
        {
            throw new BluetoothException(BluetoothFailureKind.InvalidAddress,
                $"Invalid Bluetooth address '{mac}' (expected 6 hex octets).");
        }
        return Convert.ToUInt64(digits, 16);
    }

    private static string Digits(string mac) =>
        mac.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();

    private static string StripSeparators(string s) =>
        s.Replace(":", "").Replace("-", "").ToUpperInvariant();

    private static string FormatMac(ulong address)
    {
        var hex = address.ToString("X12");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static bool HidNodePresent(string digits)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'BTHENUM%'");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["DeviceID"] is string id &&
                        id.ToUpperInvariant().Contains("DEV_" + digits))
                        return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("bt", $"HID node query failed ({ex.Message}); treating as not present");
        }
        return false;
    }

    // Classic Win32 Bluetooth "Remove device" - disconnects the active link AND removes the bond.
    // The address is a BLUETOOTH_ADDRESS union whose low 6 bytes are the MAC (passed as a ulong).
    // Returns ERROR_SUCCESS (0) on success.
    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothRemoveDevice(ref ulong pAddress);

    private static uint RemoveDevice(ulong address)
    {
        var a = address;
        return BluetoothRemoveDevice(ref a);
    }
}
