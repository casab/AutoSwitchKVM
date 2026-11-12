using System.Diagnostics;
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

    /// The most recent reconcile result (refreshed every ~2 s and on device changes), for UI that
    /// must not block on a WMI enumeration. Empty until the first reconcile.
    public IReadOnlyList<UsbDeviceInfo> LastSnapshot => Volatile.Read(ref _lastSnapshot);
    private IReadOnlyList<UsbDeviceInfo> _lastSnapshot = Array.Empty<UsbDeviceInfo>();

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
            // WMI can drop the subscription (e.g. the WMI service restarts). The 2 s reconcile
            // still converges, but arrival/departure detection gets slower — make that visible.
            _watcher.Stopped += (_, e) =>
            {
                bool started;
                lock (_gate) started = _started;
                if (started) Log.Warn("usb", $"device-change watcher stopped unexpectedly ({e.Status}); relying on the 2 s reconcile");
            };
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
        _reconcileTimer = new Timer(_ => Reconcile("periodic"), null, 0, _reconcileMs);
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

    // Burst tracking (Stopwatch ticks): detection latency was ~3.1-3.8 s behind the Mac on
    // 2026-11-12. A trailing-only debounce keeps re-arming during the KVM's multi-second
    // re-enumeration storm, so detection fell back to the 2 s periodic tick. Reconcile on the
    // leading edge too, and log the timings so the remaining lag can be attributed.
    private const int BurstQuietMs = 2000;
    private long _lastEventTicks;
    private long _burstStartTicks;

    /// Leading edge: the first event after a quiet period reconciles immediately. Trailing edge:
    /// (re)arm a one-shot debounce so the end of the storm is reconciled once.
    private void OnDeviceChange()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Exchange(ref _lastEventTicks, now);
        if (last == 0 || ElapsedMs(last, now) >= BurstQuietMs)
        {
            Interlocked.Exchange(ref _burstStartTicks, now);
            Log.Info("usb", "device-change burst started");
            ThreadPool.QueueUserWorkItem(_ => Reconcile("burst start"));
        }

        var t = new Timer(_ => Reconcile("debounce"), null, _eventDebounceMs, Timeout.Infinite);
        var old = Interlocked.Exchange(ref _debounceTimer, t);
        old?.Dispose();
    }

    private int _reconciling;
    private int _rerunRequested;

    private void Reconcile(string trigger)
    {
        // One reconcile at a time: overlapping runs (leading edge + debounce + periodic timer
        // during the KVM re-enumeration storm) can emit snapshots out of order and leave
        // _lastSignature stale. A request that arrives mid-run is not dropped: it re-runs once
        // the current one finishes, so the newest state is always enumerated.
        if (Interlocked.Exchange(ref _reconciling, 1) == 1)
        {
            Interlocked.Exchange(ref _rerunRequested, 1);
            return;
        }
        try
        {
            do
            {
                Interlocked.Exchange(ref _rerunRequested, 0);
                ReconcileOnce(trigger);
                trigger = "re-run";
            } while (Interlocked.CompareExchange(ref _rerunRequested, 0, 1) == 1);
        }
        finally
        {
            Interlocked.Exchange(ref _reconciling, 0);
        }
    }

    private void ReconcileOnce(string trigger)
    {
        lock (_gate) { if (!_started) return; }  // don't emit after Stop()

        var start = Stopwatch.GetTimestamp();
        List<UsbDeviceInfo> snapshot;
        try { snapshot = Enumerate(); }
        catch (Exception ex) { Log.Warn("usb", $"reconcile enumerate failed: {ex.Message}"); return; }
        var end = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastSnapshot, snapshot);

        var signature = new HashSet<uint>(snapshot.Select(d => ((uint)d.VendorID << 16) | d.ProductID));
        Dictionary<string, UsbDeviceInfo> previous;
        lock (_gate)
        {
            if (signature.SetEquals(_lastSignature)) return;
            _lastSignature = signature;
            previous = _lastDevices;
            _lastDevices = snapshot.Where(d => d.InstanceId is not null)
                .GroupBy(d => d.InstanceId!).ToDictionary(g => g.Key, g => g.First());
        }
        var burst = Interlocked.Read(ref _burstStartTicks);
        var sinceBurst = burst == 0 ? "" : $", {ElapsedMs(burst, end)} ms after the burst started";
        Log.Info("usb", $"snapshot changed: {snapshot.Count} USB device(s) " +
            $"(via {trigger}, enumerate {ElapsedMs(start, end)} ms{sinceBurst})");
        if (previous.Count > 0) LogDiff(previous, _lastDevices);
        Emit(snapshot);
    }

    // Which devices left/arrived in each change: shows the order in which the KVM drops and adds
    // devices, i.e. whether something leaves earlier than the configured source (a candidate for a
    // faster departure signal). Logged per VID:PID with a count, so the line stays short.
    private Dictionary<string, UsbDeviceInfo> _lastDevices = new();

    private static void LogDiff(Dictionary<string, UsbDeviceInfo> before, Dictionary<string, UsbDeviceInfo> after)
    {
        static string Describe(IEnumerable<UsbDeviceInfo> devices) => string.Join(", ",
            devices.GroupBy(d => (d.VendorID, d.ProductID))
                .OrderBy(g => g.Key.VendorID).ThenBy(g => g.Key.ProductID)
                .Select(g =>
                {
                    var name = g.Select(d => d.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "?";
                    var count = g.Count() > 1 ? $" x{g.Count()}" : "";
                    return $"{g.Key.VendorID:X4}:{g.Key.ProductID:X4} '{name}'{count}";
                }));

        var left = before.Where(kv => !after.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var arrived = after.Where(kv => !before.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        if (left.Count > 0) Log.Info("usb", $"  left ({left.Count}): {Describe(left)}");
        if (arrived.Count > 0) Log.Info("usb", $"  arrived ({arrived.Count}): {Describe(arrived)}");
    }

    private static long ElapsedMs(long fromTicks, long toTicks) =>
        (toTicks - fromTicks) * 1000 / Stopwatch.Frequency;

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
