namespace AutoSwitchKVM.Core;

/// Detects the USB device(s) that make up a KVM source by watching what changes while the user
/// switches the KVM. Ported from osx/.../USB/SourceLearner.swift, adapted to the snapshot-based
/// IUsbMonitor: snapshot the attached devices at Start; any (vendor,product) that appears or
/// disappears vs that baseline during the window becomes a candidate to review and name.
public sealed class SourceLearner
{
    private readonly IUsbMonitor _usb;
    private Dictionary<uint, UsbDeviceInfo> _baseline = new();
    private readonly HashSet<uint> _changedKeys = new();
    private Action<IReadOnlyList<UsbDeviceInfo>>? _handler;

    public bool IsLearning { get; private set; }
    public int ChangeCount => _changedKeys.Count;

    /// Fires when ChangeCount updates (so the UI can show progress).
    public event Action? Changed;

    public SourceLearner(IUsbMonitor usb) => _usb = usb;

    private static uint Key(ushort vendor, ushort product) => ((uint)vendor << 16) | product;
    private static uint Key(UsbDeviceInfo d) => Key(d.VendorID, d.ProductID);

    public void Start()
    {
        _baseline = Snapshot();
        _changedKeys.Clear();
        IsLearning = true;
        _handler = OnSnapshot;
        _usb.Changed += _handler;
        Changed?.Invoke();
    }

    private void OnSnapshot(IReadOnlyList<UsbDeviceInfo> snapshot)
    {
        var currentKeys = snapshot.Select(Key).ToHashSet();
        var baselineKeys = _baseline.Keys.ToHashSet();
        foreach (var k in currentKeys.Except(baselineKeys)) _changedKeys.Add(k);   // appeared
        foreach (var k in baselineKeys.Except(currentKeys)) _changedKeys.Add(k);   // disappeared
        Changed?.Invoke();
    }

    /// Stop learning and return the candidate devices (appeared or disappeared during the window),
    /// names resolved from the current attached list or the start-of-window snapshot.
    public IReadOnlyList<UsbDeviceInfo> Finish()
    {
        IsLearning = false;
        Unsubscribe();

        var current = Snapshot();
        var result = new List<UsbDeviceInfo>();
        foreach (var k in _changedKeys)
        {
            if (current.TryGetValue(k, out var info) || _baseline.TryGetValue(k, out info))
                result.Add(info);
            else
                result.Add(new UsbDeviceInfo((ushort)(k >> 16), (ushort)(k & 0xFFFF)));
        }
        return result.OrderBy(d => d.DisplayName, StringComparer.Ordinal).ToList();
    }

    public void Cancel()
    {
        IsLearning = false;
        Unsubscribe();
        _changedKeys.Clear();
        Changed?.Invoke();
    }

    private Dictionary<uint, UsbDeviceInfo> Snapshot()
    {
        var map = new Dictionary<uint, UsbDeviceInfo>();
        foreach (var d in _usb.Snapshot())
            map[Key(d)] = d;   // last writer wins (de-dup by vid/pid)
        return map;
    }

    private void Unsubscribe()
    {
        if (_handler != null) { _usb.Changed -= _handler; _handler = null; }
    }
}
