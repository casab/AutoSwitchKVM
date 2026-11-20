import Foundation

// BTError, BTFailureKind and BTCode (IOReturn/HCI decoding) live in BluetoothErrors.swift.

struct PairedDeviceInfo: Identifiable, Hashable {
    var name: String
    var address: String
    var id: String { address }
}

/// Backend-agnostic Bluetooth control surface. Keeps the native/private-API details
/// (and any future blueutil fallback) isolated from the rest of the app.
@MainActor
protocol BluetoothController: Sendable {
    func isConnected(_ address: String) async -> Bool
    /// Whether the device's HID connection is actually up (it is usable as an input device), as
    /// opposed to a bare baseband link: `isConnected` alone reported a link-only state as
    /// connected on hardware. `nil` when the backend can't tell (the engine then trusts
    /// `isConnected`).
    func isHIDReady(_ address: String) async -> Bool?
    /// Open a Classic connection. `pageTimeoutMs`: how long the radio calls the device (nil: the
    /// system default, ~5 s per page with retries, ~12-15 s in all on hardware).
    func connect(_ address: String, pageTimeoutMs: Int?) async throws
    /// Whether connecting this device pages it over the radio (Classic). An LE device connects by
    /// itself; "connecting" it is a wait that uses no radio.
    func pagesToConnect(_ address: String) -> Bool
    func disconnect(_ address: String) async throws
    func pair(_ address: String) async throws
    /// Whether this Mac holds a Classic bond with the device (cheap: no system_profiler). `pair`
    /// runs no ceremony when it does.
    func isPaired(_ address: String) async -> Bool
    func unpair(_ address: String) async throws
    func pairedDevices() async -> [PairedDeviceInfo]

    /// Whether the Bluetooth adapter is powered on; `nil` if it can't be determined.
    func isPoweredOn() async -> Bool?

    /// Begin observing connect/disconnect events for the given addresses. `onChange` is invoked
    /// on the main actor as `(normalizedAddress, isConnected)`. Replaces any prior monitoring.
    func startMonitoring(addresses: [String], onChange: @escaping (String, Bool) -> Void)
    func stopMonitoring()
}

extension BluetoothController {
    func connect(_ address: String) async throws { try await connect(address, pageTimeoutMs: nil) }
}

/// Claim-once gate so exactly one side of a race resumes the continuation.
private final class RaceOnce: @unchecked Sendable {
    private let lock = NSLock()
    private var claimed = false
    func claim() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if claimed { return false }
        claimed = true
        return true
    }
}

/// Run an async operation with a timeout (seconds). Throws `BTError.timeout` if it overruns.
///
/// Deliberately an *unstructured* race, not a task group: a task group cannot return until all
/// children finish, and the synchronous IOBluetooth calls never observe cancellation — a group
/// would surface the timeout only after the blocked call eventually returned (and would misreport
/// a late success as a timeout). Here the loser is cancelled cooperatively and abandoned; a late
/// completion is logged and discarded (the engine re-checks `isConnected` after a connect timeout,
/// and the status poll reconciles any state a late call changed).
///
/// On macOS every Bluetooth call is now cooperative or returns at once (connecting became
/// asynchronous on 2026-11-12; it used to block the main thread for up to 20 s, which this
/// timeout could not interrupt). `IOBluetoothController` still logs any synchronous call that holds
/// the main thread for more than 2 s.
func withTimeout<T: Sendable>(_ seconds: Int, _ operation: @escaping @Sendable () async throws -> T) async throws -> T {
    let once = RaceOnce()
    return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<T, Error>) in
        let opTask = Task {
            do {
                let value = try await operation()
                if once.claim() {
                    cont.resume(returning: value)
                } else {
                    Log.bluetooth.warn("operation completed after its \(seconds) s timeout; result discarded")
                }
            } catch {
                if once.claim() { cont.resume(throwing: error) }
            }
        }
        Task {
            try? await Task.sleep(nanoseconds: UInt64(max(0, seconds)) * 1_000_000_000)
            opTask.cancel()  // cooperative: async operations abort, blocking calls run out on their own
            if once.claim() {
                cont.resume(throwing: BTError.timeout(seconds: seconds))
            }
        }
    }
}
