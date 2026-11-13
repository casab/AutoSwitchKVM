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
/// Bluetooth LE devices (e.g. a ZMK keyboard: HID over GATT) go through the same calls. ResolveAsync
/// tries BluetoothLEDevice first, because for an LE address the Classic API returns a phantom
/// "Bluetooth xx:xx..." record that never reports Connected. LE differences: HID readiness uses the
/// BTHLEDEVICE / HID {00001812-...} nodes, and unpair uses WinRT (BluetoothRemoveDevice is Classic).
///
/// A CLI fallback (PolarGoose) remains a documented option if a future device can't be driven by
/// WinRT custom pairing.
public sealed class WinRtBluetooth : IBluetoothController
{
    private readonly DispatcherQueue? _dispatcher;
    private readonly object _gate = new();
    private readonly List<(string Digits, Action Detach)> _subs = new();
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

    // ---- Resolving an address (Classic or LE) ----

    /// The parts of a Classic BluetoothDevice or a BluetoothLEDevice the controller needs.
    private sealed class ResolvedDevice : IDisposable
    {
        public ResolvedDevice(BluetoothDevice classic) => Classic = classic;
        public ResolvedDevice(BluetoothLEDevice le) => Le = le;
        public BluetoothDevice? Classic { get; }
        public BluetoothLEDevice? Le { get; }
        public bool IsLe => Le is not null;
        public string Name => Le?.Name ?? Classic!.Name;
        public BluetoothConnectionStatus ConnectionStatus => Le?.ConnectionStatus ?? Classic!.ConnectionStatus;
        public DeviceInformation DeviceInformation => Le?.DeviceInformation ?? Classic!.DeviceInformation;
        public void Dispose()
        {
            Le?.Dispose();
            Classic?.Dispose();
        }
    }

    // Addresses seen as Bluetooth LE (a device's transport never changes), and when an address was
    // last probed as LE without an answer (re-probed now and then, e.g. after it gets paired).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _knownLe = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _leProbedAt = new();
    private static readonly TimeSpan LeReprobeInterval = TimeSpan.FromSeconds(60);

    /// Resolve an address to its Windows device record, LE first: for an LE address the Classic
    /// API returns a phantom record ("Bluetooth e7:99:...", always Disconnected; seen 2026-11-13
    /// with a ZMK keyboard), and for a Classic address the LE API returns null. null = not known
    /// to Windows (never paired / removed).
    private async Task<ResolvedDevice?> ResolveAsync(ulong addr, string digits, CancellationToken ct)
    {
        var knownLe = _knownLe.ContainsKey(digits);
        if (knownLe || !_leProbedAt.TryGetValue(digits, out var at) || DateTime.UtcNow - at > LeReprobeInterval)
        {
            var le = await BluetoothLEDevice.FromBluetoothAddressAsync(addr).AsTask(ct);
            if (le is not null)
            {
                if (_knownLe.TryAdd(digits, true))
                    Log.Debug("bt", $"{FormatMac(addr)} is a Bluetooth LE device ('{le.Name}')");
                return new ResolvedDevice(le);
            }
            if (knownLe) return null;   // an LE device Windows no longer knows; never trust the Classic phantom
            _leProbedAt[digits] = DateTime.UtcNow;
        }
        var classic = await BluetoothDevice.FromBluetoothAddressAsync(addr).AsTask(ct);
        return classic is null ? null : new ResolvedDevice(classic);
    }

    // ---- Connection state ----

    public async Task<bool> IsConnectedAsync(string address, CancellationToken ct = default)
    {
        try
        {
            using var dev = await ResolveAsync(ParseAddress(address), Digits(address), ct);
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

    // Connect == ensure paired (PairAsync is idempotent: no-op if already paired). Whether the
    // device's HID input is up is checked by the engine through IsHidReadyAsync (spec §3 connect
    // flow: wait for HID on the pairing link first, reconnect only without it).
    public Task ConnectAsync(string address, CancellationToken ct = default) => PairAsync(address, ct);

    private const string HidServiceGuid = "{00001124-0000-1000-8000-00805F9B34FB}";     // Classic HID
    private const string HogpServiceGuid = "{00001812-0000-1000-8000-00805F9B34FB}";    // HID over GATT (LE)

    private enum HidKind { None, Classic, Le }

    // Per device: which HID chain to check (its class/appearance never changes), and the last HID
    // state logged (so the log shows each change without repeating every 250 ms poll).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HidKind> _hidKind = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastHidState = new();

    /// A bond + "Connected" link alone is not a working trackpad: on 2026-11-12 a host paired it
    /// while the link was still being set up, the link came up, HID never started, and the trackpad
    /// stayed wedged. The Windows signal is the BTHENUM HID-service node plus a HID collection, both
    /// started. null for non-HID devices or when Windows doesn't know the device.
    public async Task<bool?> IsHidReadyAsync(string address, CancellationToken ct = default)
    {
        var digits = Digits(address);
        if (!_hidKind.TryGetValue(digits, out var kind))
        {
            try
            {
                using var dev = await ResolveAsync(ParseAddress(address), digits, ct);
                if (dev is null) return null;
                if (dev.Le is { } le)
                {
                    kind = le.Appearance.Category == BluetoothLEAppearanceCategories.HumanInterfaceDevice ? HidKind.Le : HidKind.None;
                    if (kind == HidKind.None)
                        Log.Debug("bt", $"hid {address}: LE device is not a HID device (appearance {le.Appearance.Category}); HID readiness not checked");
                }
                else
                {
                    var major = dev.Classic!.ClassOfDevice.MajorClass;
                    kind = major == BluetoothMajorClass.Peripheral ? HidKind.Classic : HidKind.None;
                    if (kind == HidKind.None)
                        Log.Debug("bt", $"hid {address}: not a HID peripheral ({major}); HID readiness not checked");
                }
                _hidKind[digits] = kind;
            }
            catch (OperationCanceledException) { throw; }
            catch (BluetoothException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("bt", $"hid {address}: class-of-device query failed ({ex.Message})");
                return null;
            }
        }
        if (kind == HidKind.None) return null;

        var state = await Task.Run(() => kind == HidKind.Le ? QueryLeHidState(digits) : QueryHidState(digits), ct);
        var text = state.ToString();
        if (!_lastHidState.TryGetValue(digits, out var previous) || previous != text)
        {
            _lastHidState[digits] = text;
            Log.Debug("bt", $"hid {address}: {(state.Ready ? "up" : "not up")} ({text})");
        }
        return state.Ready;
    }

    /// The HID chain for a Classic-HID device, from PnP:
    ///   BTHENUM\{00001124-...}_VID&xxxx_PID&yyyy\...&MAC_...  (Bluetooth HID service, "Bluetooth HID Device")
    ///   HID\{00001124-...}_VID&xxxx_PID&yyyy&COLnn...          (HID collections: trackpad filter, touch pad)
    /// Ready = the service node is started and at least one HID collection of that VID/PID is started.
    private readonly record struct HidState(bool ServicePresent, uint ServiceError, int ChildrenOk, int ChildrenTotal)
    {
        public bool Ready => ServicePresent && ServiceError == 0 && ChildrenOk > 0;
        public override string ToString() => !ServicePresent
            ? "no BTHENUM HID-service node"
            : $"HID-service node error code {ServiceError}, HID collections started {ChildrenOk}/{ChildrenTotal}";
    }

    private static HidState QueryHidState(string digits)
    {
        string? vidPid = null;
        uint serviceError = 0;
        var servicePresent = false;
        try
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID LIKE 'BTHENUM%'"))
            {
                foreach (ManagementBaseObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        if (mo["DeviceID"] is not string id) continue;
                        var upper = id.ToUpperInvariant();
                        if (!upper.Contains(HidServiceGuid) || !upper.Contains(digits)) continue;
                        servicePresent = true;
                        serviceError = mo["ConfigManagerErrorCode"] is uint e ? e : 0;
                        var m = System.Text.RegularExpressions.Regex.Match(upper, @"_VID&([0-9A-F]+)_PID&([0-9A-F]+)");
                        if (m.Success) vidPid = $"_VID&{m.Groups[1].Value}_PID&{m.Groups[2].Value}";
                    }
                }
            }
            if (!servicePresent || vidPid is null) return new HidState(servicePresent, serviceError, 0, 0);

            int ok = 0, total = 0;
            var childPrefix = $@"HID\{HidServiceGuid}{vidPid}";
            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID LIKE 'HID%'"))
            {
                foreach (ManagementBaseObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        if (mo["DeviceID"] is not string id) continue;
                        if (!id.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        total++;
                        if (mo["ConfigManagerErrorCode"] is uint e && e == 0) ok++;
                    }
                }
            }
            return new HidState(true, serviceError, ok, total);
        }
        catch (Exception ex)
        {
            Log.Warn("bt", $"HID state query failed ({ex.Message})");
            return new HidState(servicePresent, serviceError, 0, 0);
        }
    }

    /// The HID chain for an LE (HID over GATT) device, from PnP. Both nodes carry the address:
    ///   BTHLEDEVICE\{00001812-...}_DEV_VID&...&PID&..._<MAC>\...   ("Bluetooth Low Energy GATT compliant HID device")
    ///   HID\{00001812-...}_DEV_VID&..._<MAC>&COLnn\...           (HID collections: keyboard, consumer control)
    private static HidState QueryLeHidState(string digits)
    {
        uint serviceError = 0;
        var servicePresent = false;
        int ok = 0, total = 0;
        try
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID LIKE 'BTHLEDEVICE%' OR DeviceID LIKE 'HID%'"))
            {
                foreach (ManagementBaseObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        if (mo["DeviceID"] is not string id) continue;
                        var upper = id.ToUpperInvariant();
                        if (!upper.Contains(HogpServiceGuid) || !upper.Contains(digits)) continue;
                        var error = mo["ConfigManagerErrorCode"] is uint e ? e : 0;
                        if (upper.StartsWith("BTHLEDEVICE"))
                        {
                            servicePresent = true;
                            serviceError = error;
                        }
                        else
                        {
                            total++;
                            if (error == 0) ok++;
                        }
                    }
                }
            }
            return new HidState(servicePresent, serviceError, ok, total);
        }
        catch (Exception ex)
        {
            Log.Warn("bt", $"LE HID state query failed ({ex.Message})");
            return new HidState(servicePresent, serviceError, 0, 0);
        }
    }

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
    private const int OrphanUndoGraceMs = 3000;
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
                Log.Debug("bt", $"pair {address}: a previous pairing is still running in Windows ({orphanInfo}); not starting another");
                throw new BluetoothException(BluetoothFailureKind.OperationInProgress,
                    $"a previous pairing is still running in Windows ({orphanInfo})", "orphaned PairAsync");
            }

            // A release requested after this pair was (e.g. the source left while we queued for
            // the radio) wins: pairing now would bond the device to this PC after the handoff.
            if (ReleaseRequestedSince(digits, mySeq))
            {
                Log.Debug("bt", $"pair {address}: skipped - a release was requested after this pair was queued");
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
        var dev = await ResolveAsync(addr, Digits(address), ct);
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
            Log.Debug("bt", $"pair {address}: resolved {(dev.IsLe ? "LE " : "")}'{dev.Name}' conn={dev.ConnectionStatus} " +
                $"IsPaired={pairing.IsPaired} CanPair={pairing.CanPair} ({sw.ElapsedMilliseconds} ms)");

            if (pairing.IsPaired)
            {
                Log.Debug("bt", $"pair {address}: already paired");
                return;
            }

            custom = pairing.Custom;
            var requested = 0;   // did the device ever start the ceremony? (tells "no answer" apart)

            // Auto-accept the ceremony (a real .NET delegate; no PowerShell-style deadlock).
            handler = (_, e) =>
            {
                Interlocked.Exchange(ref requested, 1);
                Log.Debug("bt", $"pair {address}: PairingRequested kind={e.PairingKind} after {sw.ElapsedMilliseconds} ms");
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
            Log.Debug("bt", $"pair {address}: result={status} after {sw.ElapsedMilliseconds} ms" +
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
        Task<DevicePairingResult> task, string address, ResolvedDevice dev,
        DeviceInformationCustomPairing custom,
        TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs> handler,
        Stopwatch sw)
    {
        try
        {
            DevicePairingResultStatus? status = null;
            try { status = (await task).Status; }
            catch (Exception ex) { Log.Warn("bt", $"pair {address}: background pairing faulted: {ex.Message}"); }

            Log.Debug("bt", $"pair {address}: background pairing finished after {sw.Elapsed.TotalSeconds:0.0} s: " +
                (status?.ToString() ?? "error"));
            if (status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)) return;

            // The latest intent may be the engine's own stale-bond unpair, which is followed within
            // ~2 s by its next pair (review finding 7). Give a newer pair intent a moment to show up
            // before undoing a bond the engine actually wants. A real release isn't followed by a
            // pair, so it only delays the undo by this grace.
            var digits = Digits(address);
            for (var waited = 0; !WantsPaired(digits) && waited < OrphanUndoGraceMs; waited += 250)
                await Task.Delay(250);

            await _opLock.WaitAsync();
            try
            {
                if (WantsPaired(digits))
                {
                    Log.Debug("bt", $"pair {address}: late pairing kept (a connect is still wanted)");
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
        var digits = Digits(address);
        var sw = Stopwatch.StartNew();
        var isLe = false;

        // Nothing to release? (The engine's reconcile-on-seed and stale-bond paths unpair
        // idempotently.) Skip the ~3.4 s removal and the probe when Windows has neither a bond nor a
        // link. A stale "paired" record only makes us do the removal anyway, so this is safe.
        try
        {
            using var known = await ResolveAsync(addr, digits, ct);
            if (known is null ||
                (!known.DeviceInformation.Pairing.IsPaired && known.ConnectionStatus != BluetoothConnectionStatus.Connected))
            {
                Log.Debug("bt", $"unpair {address}: not paired and not connected; nothing to release ({sw.ElapsedMilliseconds} ms)");
                return;
            }
            isLe = known.IsLe;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("bt", $"unpair {address}: pre-check failed ({ex.Message}); removing anyway");
        }

        // Forcefully remove the device via the classic Win32 API (the same call behind Control
        // Panel "Remove device"). Unlike WinRT UnpairAsync - which removes the pairing record on
        // the Windows side - this also tears down the active ACL link (LMP detach), so the
        // trackpad actually lets go and the other host (Mac) can take it. WinRT unpair alone was
        // leaving the device attached on the radio.
        // Synchronous and slow (~3.4 s on hardware: it waits for the link teardown). Run it off
        // the calling thread — the engine calls from the UI thread, which would freeze the tray
        // and keep the engine's per-call timeout from firing.
        // Diagnostic only (polls WinRT + WMI every ~150 ms for 10 s): run it at Debug level only.
        // BluetoothRemoveDevice is the Classic API; an LE device is unpaired through WinRT below.
        uint? rc = null;
        if (isLe)
        {
            Log.Debug("bt", $"unpair {address}: Bluetooth LE device; using WinRT unpair");
        }
        else
        {
            if (Log.IsEnabled(LogLevel.Debug)) StartReleaseProbe(address, addr);
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
                Log.Debug("bt", $"unpair {address}: BluetoothRemoveDevice OK - link dropped and bond removed ({sw.ElapsedMilliseconds} ms)");
                RearmMonitoring(address);
                return;
            }
            if (rc == ErrorNotFound)
                Log.Debug("bt", $"unpair {address}: not in Windows' paired list (ERROR_NOT_FOUND); confirming via WinRT");
            else if (rc is not null)
                Log.Warn("bt", $"unpair {address}: BluetoothRemoveDevice failed: {Win32Name(rc.Value)}; trying WinRT unpair");
        }

        // LE, or the forceful remove didn't take: WinRT unpair.
        using (var dev = await ResolveAsync(addr, digits, ct))
        {
            if (dev?.DeviceInformation.Pairing.IsPaired == true)
            {
                var result = await dev.DeviceInformation.Pairing.UnpairAsync().AsTask(ct);
                Log.Debug("bt", $"unpair {address}: WinRT result={result.Status} ({sw.ElapsedMilliseconds} ms)");
                if (result.Status != DeviceUnpairingResultStatus.Unpaired &&
                    result.Status != DeviceUnpairingResultStatus.AlreadyUnpaired)
                {
                    var kind = result.Status == DeviceUnpairingResultStatus.OperationAlreadyInProgress
                        ? BluetoothFailureKind.OperationInProgress
                        : BluetoothFailureKind.Unknown;
                    throw new BluetoothException(kind,
                        $"unpair failed: {result.Status}" +
                        (isLe ? "" : $" (BluetoothRemoveDevice: {(rc is null ? "threw" : Win32Name(rc.Value))})"),
                        result.Status.ToString());
                }
            }
            else
            {
                Log.Debug("bt", $"unpair {address}: already unpaired ({sw.ElapsedMilliseconds} ms)");
            }
        }

        // The unpair destroyed the device record; drop (and later re-create) its subscription.
        RearmMonitoring(address);
    }

    // ---- Release probe (diagnostic) ----
    //
    // 2026-11-12, second joint run: every time the Mac paired the trackpad *during* Windows' ~3.4 s
    // BluetoothRemoveDevice (even 0.2 s before it returned), the trackpad then refused the Mac's
    // HID connection until reset. What the trackpad sees during those 3.4 s is unknown: when does
    // the link actually drop, does it reconnect to Windows (it retries its last host after a
    // release), when do the HID nodes go? The probe samples Windows' view every ~150 ms for 10 s
    // from the start of a release and logs each change with its offset, to line up against the
    // Mac's bluetoothd log. Read-only; no effect on behavior.

    private CancellationTokenSource? _probeCts;
    private const int ReleaseProbeMs = 10_000;

    private void StartReleaseProbe(string address, ulong addr)
    {
        var cts = new CancellationTokenSource(ReleaseProbeMs);
        Interlocked.Exchange(ref _probeCts, cts)?.Cancel();
        _ = ReleaseProbeAsync(address, addr, Digits(address), cts.Token);
    }

    private static async Task ReleaseProbeAsync(string address, ulong addr, string digits, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? last = null;
        HidState hid = default;
        var iteration = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string state;
                try
                {
                    if (iteration++ % 3 == 0)   // WMI is slower; sample HID every third tick
                        hid = await Task.Run(() => QueryHidState(digits), ct);
                    using var dev = await BluetoothDevice.FromBluetoothAddressAsync(addr).AsTask(ct);
                    var link = dev is null ? "no-record" : dev.ConnectionStatus.ToString();
                    var paired = dev is null ? "-" : dev.DeviceInformation.Pairing.IsPaired.ToString();
                    var hidText = !hid.ServicePresent ? "gone" : hid.Ready ? "up" : $"partial({hid})";
                    state = $"link={link} paired={paired} hid={hidText}";
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { state = $"query failed: {ex.Message}"; }

                if (state != last)
                {
                    Log.Debug("bt", $"release probe {address} +{sw.ElapsedMilliseconds} ms: {state}");
                    last = state;
                }
                await Task.Delay(150, ct);
            }
        }
        catch (OperationCanceledException) { /* probe window over */ }
        Log.Debug("bt", $"release probe {address}: ended at +{sw.ElapsedMilliseconds} ms (last: {last})");
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
            var found = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)).AsTask(ct);
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
            Log.Warn("bt", $"paired list: Classic enumeration failed ({ex.Message})");
        }

        // Bluetooth LE devices (e.g. BLE keyboards) are a separate list in Windows.
        try
        {
            var found = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask(ct);
            foreach (var di in found)
            {
                try
                {
                    using var dev = await BluetoothLEDevice.FromIdAsync(di.Id).AsTask(ct);
                    if (dev is null) continue;
                    var mac = FormatMac(dev.BluetoothAddress);
                    if (list.Any(p => p.Address == mac)) continue;   // dual-mode device already listed
                    var name = string.IsNullOrEmpty(dev.Name) ? di.Name : dev.Name;
                    list.Add(new PairedDeviceInfo(name, mac, IsLowEnergy: true));
                }
                catch (Exception ex)
                {
                    Log.Warn("bt", $"paired list: skipping LE '{di.Name}' ({ex.Message})");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("bt", $"paired list: LE enumeration failed ({ex.Message})");
        }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
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
        Log.Always("diag", $"===== Diagnose {address} (match digits: {digits}) =====");

        try
        {
            var radios = await Radio.GetRadiosAsync();
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            Log.Always("diag", $"radio: {(bt is null ? "<none>" : bt.State.ToString())}");
        }
        catch (Exception ex) { Log.Error("diag", "radio query failed", ex); }

        lock (_gate)
        {
            if (_orphanPair is { IsCompleted: false })
                Log.Always("diag", $"a background pairing of {_orphanAddress} is still running");
        }

        try
        {
            var addr = ParseAddress(address);
            Log.Always("diag", $"parsed address = 0x{addr:X12}");
            using var dev = await BluetoothDevice.FromBluetoothAddressAsync(addr);
            if (dev is null)
            {
                Log.Always("diag", "FromBluetoothAddressAsync: returned NULL (device not known to the system by address)");
            }
            else
            {
                var p = dev.DeviceInformation.Pairing;
                Log.Always("diag", $"FromBluetoothAddressAsync: name='{dev.Name}' conn={dev.ConnectionStatus} " +
                    $"IsPaired={p.IsPaired} CanPair={p.CanPair} kind={dev.DeviceInformation.Kind} id={dev.DeviceInformation.Id}");
            }
        }
        catch (Exception ex) { Log.Error("diag", "FromBluetoothAddressAsync failed", ex); }

        try
        {
            using var le = await BluetoothLEDevice.FromBluetoothAddressAsync(ParseAddress(address));
            Log.Always("diag", le is null
                ? "BluetoothLEDevice.FromBluetoothAddressAsync: NULL (not a known LE device)"
                : $"BluetoothLEDevice: name='{le.Name}' conn={le.ConnectionStatus} addressType={le.BluetoothAddressType} " +
                  $"IsPaired={le.DeviceInformation.Pairing.IsPaired} appearance={le.Appearance.Category}/{le.Appearance.SubCategory} id={le.DeviceId}");
        }
        catch (Exception ex) { Log.Error("diag", "BluetoothLEDevice.FromBluetoothAddressAsync failed", ex); }
        await DumpSelectorAsync("le-paired", BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), digits);

        await DumpSelectorAsync("paired", BluetoothDevice.GetDeviceSelectorFromPairingState(true), digits);
        await DumpSelectorAsync("unpaired", BluetoothDevice.GetDeviceSelectorFromPairingState(false), digits);
        try { await DumpSelectorAsync("bt-classic-any", BluetoothDevice.GetDeviceSelector(), digits); }
        catch (Exception ex) { Log.Warn("diag", $"bt-classic-any selector failed: {ex.Message}"); }

        Log.Always("diag", $"Bluetooth PnP node present for MAC: {HidNodePresent(digits)}; LE HID chain: {QueryLeHidState(digits)}");
        Log.Always("diag", "===== end Diagnose =====");
    }

    private static async Task DumpSelectorAsync(string label, string selector, string digits)
    {
        try
        {
            var found = await DeviceInformation.FindAllAsync(selector);
            Log.Always("diag", $"[{label}] {found.Count} device(s):");
            foreach (var d in found)
            {
                var hit = StripSeparators(d.Id).Contains(digits, StringComparison.OrdinalIgnoreCase) ? "  <== TARGET" : "";
                Log.Always("diag", $"  [{label}] name='{d.Name}' id={d.Id}{hit}");
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
            var dev = await ResolveAsync(ParseAddress(address), digits, ct);
            if (dev is null)
            {
                Log.Debug("bt", $"monitor {address}: not known to Windows yet (unpaired); events resume after the next pair");
                return;
            }

            // Read the status in the event handler, not inside the dispatched lambda: StopMonitoring
            // disposes the device, and a queued lambda touching a disposed sender throws
            // ObjectDisposedException on the UI thread.
            void Report(Func<BluetoothConnectionStatus> status)
            {
                bool isConnected;
                try { isConnected = status() == BluetoothConnectionStatus.Connected; }
                catch { return; }  // device already disposed/removed mid-event
                Post(() => onChange(new ConnectionChange(digits, isConnected)));
            }

            Action detach;
            if (dev.Le is { } le)
            {
                TypedEventHandler<BluetoothLEDevice, object> handler = (sender, _) => Report(() => sender.ConnectionStatus);
                le.ConnectionStatusChanged += handler;
                detach = () => { le.ConnectionStatusChanged -= handler; le.Dispose(); };
            }
            else
            {
                var classic = dev.Classic!;
                TypedEventHandler<BluetoothDevice, object> handler = (sender, _) => Report(() => sender.ConnectionStatus);
                classic.ConnectionStatusChanged += handler;
                detach = () => { classic.ConnectionStatusChanged -= handler; classic.Dispose(); };
            }

            lock (_gate)
            {
                if (ct.IsCancellationRequested)
                {
                    try { detach(); } catch { /* ignore */ }
                    return;
                }
                _subs.Add((digits, detach));
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
                if (_subs[i].Digits != digits) continue;
                try { _subs[i].Detach(); } catch { /* ignore */ }
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
            foreach (var sub in _subs)
            {
                try { sub.Detach(); } catch { /* ignore */ }
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
                "SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'BTHENUM%' OR DeviceID LIKE 'BTHLE%'");
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
