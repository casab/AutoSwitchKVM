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
///   PairAsync       = discover the unpaired endpoint + Custom.PairAsync(ConfirmOnly) auto-accept.
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

    public async Task PairAsync(string address, CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct);
        try
        {
            // FromBluetoothAddressAsync resolves the pairable AssociationEndpoint INSTANTLY (~10ms) -
            // the exact same endpoint a 30s inquiry would "discover" (verified: identical Id, CanPair=
            // True). So we pair it directly; no DeviceWatcher / FindAllAsync inquiry needed.
            using var dev = await BluetoothDevice.FromBluetoothAddressAsync(ParseAddress(address)).AsTask(ct);
            if (dev is null)
                throw new BluetoothException($"{address}: device not known to the system (turn it on / bring it in range).");

            var pairing = dev.DeviceInformation.Pairing;
            Log.Info("bt", $"pair {address}: resolved '{dev.Name}' conn={dev.ConnectionStatus} " +
                $"IsPaired={pairing.IsPaired} CanPair={pairing.CanPair}");

            if (pairing.IsPaired)
            {
                Log.Info("bt", $"pair {address}: already paired");
                return;
            }

            var custom = pairing.Custom;

            // Auto-accept the ceremony (a real .NET delegate; no PowerShell-style deadlock).
            TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs> handler =
                (_, e) =>
                {
                    Log.Info("bt", $"pair {address}: PairingRequested kind={e.PairingKind}");
                    if (e.PairingKind == DevicePairingKinds.ProvidePin) e.Accept("0000");
                    else e.Accept();
                };
            custom.PairingRequested += handler;
            try
            {
                // The trackpad uses ConfirmOnly; accept the common HID ceremonies. Do NOT gate on CanPair.
                const DevicePairingKinds kinds = DevicePairingKinds.ConfirmOnly
                    | DevicePairingKinds.ProvidePin
                    | DevicePairingKinds.ConfirmPinMatch
                    | DevicePairingKinds.DisplayPin;
                var result = await custom.PairAsync(kinds).AsTask(ct);
                Log.Info("bt", $"pair {address}: result={result.Status}");
                if (result.Status != DevicePairingResultStatus.Paired &&
                    result.Status != DevicePairingResultStatus.AlreadyPaired)
                {
                    throw new BluetoothException($"Pair {address} failed: {result.Status}");
                }
                // Pairing created a fresh device record — the old ConnectionStatusChanged
                // subscription (if any) points at a dead object.
                RearmMonitoring(address);
            }
            finally
            {
                custom.PairingRequested -= handler;
            }
        }
        finally
        {
            _opLock.Release();
        }
    }

    public async Task UnpairAsync(string address, CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct);
        try
        {
            var addr = ParseAddress(address);

            // Forcefully remove the device via the classic Win32 API (the same call behind Control
            // Panel "Remove device"). Unlike WinRT UnpairAsync - which removes the pairing record on
            // the Windows side - this also tears down the active ACL link (LMP detach), so the
            // trackpad actually lets go and the other host (Mac) can take it. WinRT unpair alone was
            // leaving the device attached on the radio.
            uint rc = uint.MaxValue;
            try
            {
                var a = addr;
                rc = BluetoothRemoveDevice(ref a);
                Log.Info("bt", rc == 0
                    ? $"unpair {address}: BluetoothRemoveDevice OK (device removed + disconnected)"
                    : $"unpair {address}: BluetoothRemoveDevice rc=0x{rc:X} (will try WinRT unpair)");
            }
            catch (Exception ex)
            {
                Log.Warn("bt", $"unpair {address}: BluetoothRemoveDevice threw: {ex.Message}");
            }

            // Fallback: if the forceful remove didn't take, fall back to WinRT unpair.
            if (rc != 0)
            {
                using var dev = await BluetoothDevice.FromBluetoothAddressAsync(addr).AsTask(ct);
                if (dev?.DeviceInformation.Pairing.IsPaired == true)
                {
                    var result = await dev.DeviceInformation.Pairing.UnpairAsync().AsTask(ct);
                    Log.Info("bt", $"unpair {address}: WinRT result={result.Status}");
                    if (result.Status != DeviceUnpairingResultStatus.Unpaired &&
                        result.Status != DeviceUnpairingResultStatus.AlreadyUnpaired)
                    {
                        throw new BluetoothException($"Unpair {address} failed: {result.Status}");
                    }
                }
                else
                {
                    Log.Info("bt", $"unpair {address}: already unpaired");
                }
            }

            // The unpair destroyed the device record; drop (and later re-create) its subscription.
            RearmMonitoring(address);
        }
        finally
        {
            _opLock.Release();
        }
    }

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
                catch { /* skip a device that won't resolve */ }
            }
        }
        catch { /* enumeration unavailable -> empty list */ }
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
            if (dev is null) return;

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
        catch { /* skip an address we can't resolve */ }
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
            throw new BluetoothException($"Invalid Bluetooth address '{mac}' (expected 6 hex octets).");
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
        catch { /* WMI hiccup -> treat as not present */ }
        return false;
    }

    // Classic Win32 Bluetooth "Remove device" - disconnects the active link AND removes the bond.
    // The address is a BLUETOOTH_ADDRESS union whose low 6 bytes are the MAC (passed as a ulong).
    // Returns ERROR_SUCCESS (0) on success.
    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothRemoveDevice(ref ulong pAddress);
}
