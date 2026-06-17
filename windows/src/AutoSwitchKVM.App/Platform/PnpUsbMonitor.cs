using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using AutoSwitchKVM.App.Support;
using AutoSwitchKVM.Core;
using Microsoft.UI.Dispatching;

namespace AutoSwitchKVM.App.Platform;

/// IUsbMonitor via WMI. Design validated on real hardware in Milestone 0 (windows/spikes/Spike3-Usb.ps1):
///   - Enumerate USB devices from Win32_PnPEntity, parse VID/PID from the DeviceID.
///   - Watch Win32_DeviceChangeEvent as *wakeups* only (a KVM switch produced ~113 events in 60s),
///     each arming a short debounce that triggers one reconcile.
///   - A ~2s periodic reconcile is the safety-net that guarantees convergence through the USB
///     re-enumeration storm.
///   - Emit a snapshot only when the set of (VID,PID) actually changes, to avoid churn.
///
/// Threading: WMI callbacks and the timers run on background threads. Changed is marshaled to the
/// SynchronizationContext captured at construction (the UI thread, when constructed there), so the
/// engine - which assumes single-threaded use - is touched on the right thread.
public sealed class PnpUsbMonitor : IUsbMonitor, IDisposable
{
    private static readonly Regex VidPid =
        new(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    private readonly int _reconcileMs;
    private readonly int _eventDebounceMs;
    private readonly DispatcherQueue? _dispatcher;
    private readonly object _gate = new();

    private ManagementEventWatcher? _watcher;
    private Timer? _reconcileTimer;
    private Timer? _debounceTimer;
    private HashSet<uint> _lastSignature = new();
    private bool _started;

    public event Action<IReadOnlyList<UsbDeviceInfo>>? Changed;

    /// Pass the UI DispatcherQueue so Changed is raised on the UI thread (the engine is single-threaded).
    public PnpUsbMonitor(DispatcherQueue? dispatcher = null, int reconcileMs = 2000, int eventDebounceMs = 500)
    {
        _dispatcher = dispatcher;
        _reconcileMs = reconcileMs;
        _eventDebounceMs = eventDebounceMs;
    }

    public IReadOnlyList<UsbDeviceInfo> Snapshot() => Enumerate();

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        try
        {
            _watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent"));
            _watcher.EventArrived += (_, _) => OnDeviceChange();
            _watcher.Start();
            Log.Info("usb", "device-change watcher started");
        }
        catch (Exception ex)
        {
            // If WMI events are unavailable, the reconcile timer alone still converges.
            _watcher = null;
            Log.Warn("usb", $"device-change watcher unavailable, using reconcile only: {ex.Message}");
        }

        // Immediate first reconcile (dueTime 0) emits the initial snapshot, then every _reconcileMs.
        _reconcileTimer = new Timer(_ => Reconcile(), null, 0, _reconcileMs);
    }

    public void Stop()
    {
        lock (_gate) { _started = false; }
        try { _watcher?.Stop(); } catch { /* ignore */ }
        _watcher?.Dispose(); _watcher = null;
        _reconcileTimer?.Dispose(); _reconcileTimer = null;
        _debounceTimer?.Dispose(); _debounceTimer = null;
    }

    public void Dispose() => Stop();

    /// Coalesce the device-change storm: (re)arm a one-shot debounce that triggers a single reconcile.
    private void OnDeviceChange()
    {
        var t = new Timer(_ => Reconcile(), null, _eventDebounceMs, Timeout.Infinite);
        var old = Interlocked.Exchange(ref _debounceTimer, t);
        old?.Dispose();
    }

    private void Reconcile()
    {
        List<UsbDeviceInfo> snapshot;
        try { snapshot = Enumerate(); }
        catch (Exception ex) { Log.Warn("usb", $"reconcile enumerate failed: {ex.Message}"); return; }

        var signature = new HashSet<uint>(snapshot.Select(d => ((uint)d.VendorID << 16) | d.ProductID));
        lock (_gate)
        {
            if (signature.SetEquals(_lastSignature)) return;
            _lastSignature = signature;
        }
        Log.Info("usb", $"snapshot changed: {snapshot.Count} USB device(s)");
        Emit(snapshot);
    }

    private void Emit(IReadOnlyList<UsbDeviceInfo> snapshot)
    {
        var handler = Changed;
        if (handler is null) return;
        if (_dispatcher != null) _dispatcher.TryEnqueue(() => handler(snapshot));
        else handler(snapshot);
    }

    private static List<UsbDeviceInfo> Enumerate()
    {
        var list = new List<UsbDeviceInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Name FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'");
        foreach (ManagementBaseObject mo in searcher.Get())
        {
            using (mo)
            {
                if (mo["DeviceID"] is not string id) continue;
                var m = VidPid.Match(id);
                if (!m.Success) continue;
                var vid = ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var pid = ushort.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var name = mo["Name"] as string;
                list.Add(new UsbDeviceInfo(vid, pid, name, id));
            }
        }
        return list;
    }
}
