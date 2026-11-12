namespace AutoSwitchKVM.Core;

/// Lightweight view of a paired device (name + address).
public readonly record struct PairedDeviceInfo(string Name, string Address);

/// Raised when a monitored device's connection state changes.
/// <param name="Address">Normalized (colon-less, upper) address digits.</param>
public readonly record struct ConnectionChange(string Address, bool IsConnected);

/// Backend-agnostic Bluetooth control surface. Mirrors the macOS `BluetoothController` protocol
/// (osx/.../Bluetooth/BluetoothController.swift) and SPECIFICATION.md section 5. Keeps WinRT / any
/// CLI-fallback details out of the engine.
///
/// Windows mapping (validated in Milestone 0, see windows/PLAN.md):
///   - Connect    = ensure PAIRED (Classic-HID auto-connects on pairing). Pair = discover the
///                  unpaired endpoint + Custom.PairAsync(ConfirmOnly) with an auto-accept handler.
///   - Disconnect = UNPAIR (remove bond) - there is no separate disconnect for Classic HID, and the
///                  bond is exclusive (the other host can't take the device until we release it).
///   - IsConnected= BluetoothDevice.ConnectionStatus (primary) corroborated by BTHENUM HID nodes.
///   - IsPoweredOn= Radio.State of the Bluetooth radio.
public interface IBluetoothController
{
    Task<bool> IsConnectedAsync(string address, CancellationToken ct = default);

    /// Whether the device's HID input is actually up (usable as an input device), as opposed to a
    /// bare link: a "Connected" link alone was not a working trackpad on hardware. null when the
    /// backend can't tell or the device isn't a HID peripheral (the engine then trusts
    /// IsConnectedAsync). Mirrors macOS `isHIDReady`.
    Task<bool?> IsHidReadyAsync(string address, CancellationToken ct = default);

    /// Bring the device up for this host (on Windows: ensure paired).
    Task ConnectAsync(string address, CancellationToken ct = default);

    /// Release the device from this host (on Windows: unpair / remove bond).
    Task DisconnectAsync(string address, CancellationToken ct = default);

    Task PairAsync(string address, CancellationToken ct = default);
    Task UnpairAsync(string address, CancellationToken ct = default);

    Task<IReadOnlyList<PairedDeviceInfo>> PairedDevicesAsync(CancellationToken ct = default);

    /// Whether the Bluetooth adapter is powered on; null if it can't be determined.
    Task<bool?> IsPoweredOnAsync(CancellationToken ct = default);

    /// Begin observing connect/disconnect events for the given addresses. Replaces prior monitoring.
    void StartMonitoring(IEnumerable<string> addresses, Action<ConnectionChange> onChange);
    void StopMonitoring();
}

/// Why a Bluetooth operation failed. Backends classify what they can; the engine picks its
/// retry strategy from this (see SelectionEngine.ConnectOneAsync and SPECIFICATION.md §3
/// "Failure classification"). A backend that can't tell reports Unknown, which gets the
/// generic behavior (exponential backoff + stale-bond re-pair on retry).
public enum BluetoothFailureKind
{
    /// Not classified — generic retry with backoff (and a fresh bond on the next try).
    Unknown,

    /// The device answered but refused (Windows: AuthenticationNotAllowed, ConnectionRejected,
    /// NotReadyToPair, ...). For an exclusive-bond device this almost always means the other
    /// host still holds it and is about to release: retry soon, don't escalate.
    DeviceBusy,

    /// An earlier OS-level operation on the radio/device is still running (Windows:
    /// OperationAlreadyInProgress). Wait for it; retrying immediately only collides again.
    OperationInProgress,

    /// The device never answered in time (our per-call timeout, or Windows AuthenticationTimeout).
    Timeout,

    /// Key exchange failed — typically a stale/mismatched link key. Re-pair from scratch.
    AuthenticationFailed,

    /// The system doesn't know the device (never paired here / out of range / powered off).
    DeviceNotFound,

    /// The local adapter is off, missing, or failed.
    AdapterUnavailable,

    /// The configured address is malformed — retrying can never succeed.
    InvalidAddress,

    /// The link/bond came up but the device never started its HID input (Windows: the BTHENUM
    /// HID-service node or its HID child never started). Seen on hardware when the trackpad was
    /// paired while the other host was still releasing it: it then stayed wedged until power-
    /// cycled. Hard, no stale-bond re-pair; repeated occurrences end the run early.
    HidNotReady,
}

/// Thrown by IBluetoothController implementations for expected, message-bearing failures. Kind
/// drives the engine's retry policy; Detail carries the raw platform status for the log.
public sealed class BluetoothException : Exception
{
    public BluetoothFailureKind Kind { get; }

    /// Raw platform status (e.g. "AuthenticationNotAllowed", "rc=0x490"), if any.
    public string? Detail { get; }

    public BluetoothException(string message) : this(BluetoothFailureKind.Unknown, message) { }
    public BluetoothException(string message, Exception inner) : this(BluetoothFailureKind.Unknown, message, null, inner) { }

    public BluetoothException(BluetoothFailureKind kind, string message, string? detail = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Detail = detail;
    }
}
