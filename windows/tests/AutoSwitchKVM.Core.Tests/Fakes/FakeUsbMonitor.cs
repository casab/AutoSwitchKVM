using AutoSwitchKVM.Core;

namespace AutoSwitchKVM.Core.Tests.Fakes;

/// In-memory IUsbMonitor for engine tests. SetDevices simulates an attach/detach and raises Changed
/// with the new snapshot, the same way PnpUsbMonitor will at runtime.
public sealed class FakeUsbMonitor : IUsbMonitor
{
    private List<UsbDeviceInfo> _devices = new();

    public event Action<IReadOnlyList<UsbDeviceInfo>>? Changed;

    /// When set, Snapshot() throws it (models a WMI failure, e.g. right after wake).
    public Exception? SnapshotError { get; set; }

    public IReadOnlyList<UsbDeviceInfo> Snapshot() =>
        SnapshotError is not null ? throw SnapshotError : _devices.ToList();

    public void Start() { }
    public void Stop() { }

    /// Replace the attached set and notify listeners (simulates a KVM switch).
    public void SetDevices(params UsbDeviceInfo[] devices)
    {
        _devices = devices.ToList();
        Changed?.Invoke(Snapshot());
    }
}
