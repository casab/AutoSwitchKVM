using System.Management;
using AutoSwitchKVM.Core;
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
///   UnpairAsync     = Pairing.UnpairAsync().
///   IsConnected     = ConnectionStatus == Connected (primary), corroborated by a BTHENUM HID node.
///   IsPoweredOn     = Bluetooth Radio.State == On.
///
/// A CLI fallback (PolarGoose) remains a documented option if a future device can't be driven by
/// WinRT custom pairing.
public sealed class WinRtBluetooth : IBluetoothController
{
    private readonly SynchronizationContext? _sync;
    private readonly object _gate = new();
    private readonly List<(BluetoothDevice dev, TypedEventHandler<BluetoothDevice, object> handler)> _subs = new();

    public WinRtBluetooth()
    {
        _sync = SynchronizationContext.Current;
    }

    // ---- Power ----

    public async Task<bool?> IsPoweredOnAsync(CancellationToken ct = default)
    {
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask(ct);
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            return bt is null ? null : bt.State == RadioState.On;
        }
        catch
        {
            return null; // access not granted / no radio -> unknown
        }
    }

    // ---- Connection state ----

    public async Task<bool> IsConnectedAsync(string address, CancellationToken ct = default)
    {
        try
        {
            using var dev = await BluetoothDevice.FromBluetoothAddressAsync(ParseAddress(address)).AsTask(ct);
            if (dev != null && dev.ConnectionStatus == BluetoothConnectionStatus.Connected)
                return true;
        }
        catch { /* fall through to HID check */ }

        return HidNodePresent(Digits(address));
    }

    // ---- Connect / disconnect (Classic-HID semantics; see class doc) ----

    // Connect == ensure paired (PairAsync is idempotent: no-op if already paired).
    public Task ConnectAsync(string address, CancellationToken ct = default) => PairAsync(address, ct);

    public Task DisconnectAsync(string address, CancellationToken ct = default) => Task.CompletedTask;

    // ---- Pair / unpair ----

    public async Task PairAsync(string address, CancellationToken ct = default)
    {
        var digits = Digits(address);

        // Idempotent: if already paired (== connected when in range), nothing to do. The engine
        // calls pair() on every connect attempt, so this must not fail on an already-paired device.
        try
        {
            using var existing = await BluetoothDevice.FromBluetoothAddressAsync(ParseAddress(address)).AsTask(ct);
            if (existing?.DeviceInformation.Pairing.IsPaired == true) return;
        }
        catch { /* resolve failure -> attempt discovery + pair below */ }

        // The cached device is NOT pairable; discover the freshly-enumerated unpaired endpoint.
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(false);
        var found = await DeviceInformation.FindAllAsync(selector).AsTask(ct);
        var di = found.FirstOrDefault(d =>
            StripSeparators(d.Id).Contains(digits, StringComparison.OrdinalIgnoreCase));

        if (di is null)
            throw new BluetoothException(
                $"{address} is not discoverable as an unpaired endpoint - free it from the other host / put it in pairing mode.");

        var custom = di.Pairing.Custom;

        // Auto-accept the ConfirmOnly ceremony (a real .NET delegate; no PowerShell-style deadlock).
        TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs> handler =
            (_, e) =>
            {
                if (e.PairingKind == DevicePairingKinds.ProvidePin) e.Accept("0000");
                else e.Accept();
            };
        custom.PairingRequested += handler;
        try
        {
            // Do NOT gate on CanPair (it reads false yet pairing succeeds).
            var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly).AsTask(ct);
            if (result.Status != DevicePairingResultStatus.Paired &&
                result.Status != DevicePairingResultStatus.AlreadyPaired)
            {
                throw new BluetoothException($"Pair {address} failed: {result.Status}");
            }
        }
        finally
        {
            custom.PairingRequested -= handler;
        }
    }

    public async Task UnpairAsync(string address, CancellationToken ct = default)
    {
        using var dev = await BluetoothDevice.FromBluetoothAddressAsync(ParseAddress(address)).AsTask(ct);
        if (dev is null) return;
        var pairing = dev.DeviceInformation.Pairing;
        if (!pairing.IsPaired) return;

        var result = await pairing.UnpairAsync().AsTask(ct);
        if (result.Status != DeviceUnpairingResultStatus.Unpaired &&
            result.Status != DeviceUnpairingResultStatus.AlreadyUnpaired)
        {
            throw new BluetoothException($"Unpair {address} failed: {result.Status}");
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

    // ---- Monitoring (best-effort; the engine also polls as a safety-net) ----

    public void StartMonitoring(IEnumerable<string> addresses, Action<ConnectionChange> onChange)
    {
        StopMonitoring();
        _ = SetupMonitoringAsync(addresses.ToList(), onChange);
    }

    private async Task SetupMonitoringAsync(List<string> addresses, Action<ConnectionChange> onChange)
    {
        foreach (var address in addresses)
        {
            try
            {
                var digits = Digits(address);
                var dev = await BluetoothDevice.FromBluetoothAddressAsync(ParseAddress(address));
                if (dev is null) continue;

                TypedEventHandler<BluetoothDevice, object> handler = (sender, _) =>
                    Post(() => onChange(new ConnectionChange(
                        digits, sender.ConnectionStatus == BluetoothConnectionStatus.Connected)));

                dev.ConnectionStatusChanged += handler;
                lock (_gate) { _subs.Add((dev, handler)); }
            }
            catch { /* skip an address we can't resolve */ }
        }
    }

    public void StopMonitoring()
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
        if (_sync != null) _sync.Post(_ => action(), null);
        else action();
    }

    /// MAC string ("3C:50:02:BF:22:45" or "3c-50-...") -> 64-bit Bluetooth address for the WinRT API.
    internal static ulong ParseAddress(string mac) => Convert.ToUInt64(Digits(mac), 16);

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
}
