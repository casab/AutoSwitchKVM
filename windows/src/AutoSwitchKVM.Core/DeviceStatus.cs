namespace AutoSwitchKVM.Core;

public enum DeviceStatusKind
{
    Idle,
    Connecting,
    Connected,
    Disconnected,
    BluetoothOff,
    Error,
}

/// Per-device status. Mirrors the macOS `DeviceStatus` enum (value-equatable; `Error` carries a
/// message). A record struct gives value equality so callers can compare statuses directly.
public readonly record struct DeviceStatus(DeviceStatusKind Kind, string? Message = null)
{
    public static readonly DeviceStatus Idle = new(DeviceStatusKind.Idle);
    public static readonly DeviceStatus Connecting = new(DeviceStatusKind.Connecting);
    public static readonly DeviceStatus Connected = new(DeviceStatusKind.Connected);
    public static readonly DeviceStatus Disconnected = new(DeviceStatusKind.Disconnected);
    public static readonly DeviceStatus BluetoothOff = new(DeviceStatusKind.BluetoothOff);

    public static DeviceStatus Error(string message) => new(DeviceStatusKind.Error, message);

    public string Label => Kind switch
    {
        DeviceStatusKind.Idle => "Idle",
        DeviceStatusKind.Connecting => "Connecting...",
        DeviceStatusKind.Connected => "Connected",
        DeviceStatusKind.Disconnected => "Disconnected",
        DeviceStatusKind.BluetoothOff => "Bluetooth off",
        DeviceStatusKind.Error => $"Error: {Message}",
        _ => "",
    };
}
