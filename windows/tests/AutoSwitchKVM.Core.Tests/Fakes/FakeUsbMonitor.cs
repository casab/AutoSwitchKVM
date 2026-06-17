using AutoSwitchKVM.Core;

namespace AutoSwitchKVM.Core.Tests.Fakes;

/// In-memory IUsbMonitor for engine tests. SetDevices simulates an attach/detach and raises Changed
/// with the new snapshot, the same way PnpUsbMonitor will at runtime.
public sealed class FakeUsbMonitor : IUsbMonitor
{
    private List<UsbDeviceInfo> _devices = new();

    public event Action<IReadOnlyList<UsbDeviceInfo>>? Changed;

    public IReadOnlyList<UsbDeviceInfo> Snapshot() => _devices.ToList();

    public void Start() { }
    public void Stop() { }

    /// Replace the attached set and notify listeners (simulates a KVM switch).
    public void SetDevices(params UsbDeviceInfo[] devices)
    {
        _devices = devices.ToList();
        Changed?.Invoke(Snapshot());
    }
}
