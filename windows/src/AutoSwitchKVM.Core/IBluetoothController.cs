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

/// Thrown by IBluetoothController implementations for expected, message-bearing failures
/// (the engine treats these as retryable per its backoff policy).
public sealed class BluetoothException : Exception
{
    public BluetoothException(string message) : base(message) { }
    public BluetoothException(string message, Exception inner) : base(message, inner) { }
}
