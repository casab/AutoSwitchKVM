namespace AutoSwitchKVM.Core;

/// A currently-attached USB device. The engine matches on VendorID/ProductID; Name/InstanceId are
/// for the settings dropdown and Learn-source UI.
public readonly record struct UsbDeviceInfo(
    ushort VendorID, ushort ProductID, string? Name = null, string? InstanceId = null)
{
    public string DisplayName =>
        $"{(string.IsNullOrEmpty(Name) ? "Unknown USB device" : Name)}  (0x{VendorID:X4}:0x{ProductID:X4})";
}

/// Backend-agnostic USB attach/detach monitor. Mirrors the macOS USBMonitor (IOKit) role.
///
/// Windows implementation (PnpUsbMonitor, Milestone 3): a DeviceWatcher / CfgMgr32 notification
/// source parsing USB\VID_xxxx&PID_xxxx, PLUS a ~2s reconcile timer - the KVM's USB re-enumeration
/// storm starves a pure debounce, so a steady idempotent reconcile guarantees convergence
/// (lesson from reference/Trackpad-AutoSwitch.ps1). Emits the current snapshot on Added/Removed.
public interface IUsbMonitor
{
    /// Snapshot of attached devices right now.
    IReadOnlyList<UsbDeviceInfo> Snapshot();

    /// Raised on attach/removal and on each reconcile tick. Carries the fresh full snapshot so the
    /// engine can recompute presence idempotently.
    event Action<IReadOnlyList<UsbDeviceInfo>> Changed;

    void Start();
    void Stop();
}
