import Foundation

enum BTError: Error, CustomStringConvertible {
    case invalidAddress(String)
    case connectFailed(Int32)
    case disconnectFailed(Int32)
    case pairFailed(String)
    case unpairUnavailable
    case unpairFailed(Int32)
    case timeout

    var description: String {
        switch self {
        case .invalidAddress(let a): return "Invalid Bluetooth address: \(a)"
        case .connectFailed(let c): return "Connect failed (IOReturn \(c))"
        case .disconnectFailed(let c): return "Disconnect failed (IOReturn \(c))"
        case .pairFailed(let m): return "Pairing failed: \(m)"
        case .unpairUnavailable: return "Unpair API unavailable on this macOS version"
        case .unpairFailed(let c): return "Unpair failed (IOReturn \(c))"
        case .timeout: return "Operation timed out"
        }
    }
}

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
    func connect(_ address: String) async throws
    func disconnect(_ address: String) async throws
    func pair(_ address: String) async throws
    func unpair(_ address: String) async throws
    func pairedDevices() async -> [PairedDeviceInfo]

    /// Whether the Bluetooth adapter is powered on; `nil` if it can't be determined.
    func isPoweredOn() async -> Bool?

    /// Begin observing connect/disconnect events for the given addresses. `onChange` is invoked
    /// on the main actor as `(normalizedAddress, isConnected)`. Replaces any prior monitoring.
    func startMonitoring(addresses: [String], onChange: @escaping (String, Bool) -> Void)
    func stopMonitoring()
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
func withTimeout<T: Sendable>(_ seconds: Int, _ operation: @escaping @Sendable () async throws -> T) async throws -> T {
    let once = RaceOnce()
    return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<T, Error>) in
        let opTask = Task {
            do {
                let value = try await operation()
                if once.claim() {
                    cont.resume(returning: value)
                } else {
                    Log.bluetooth.log("operation completed after its \(seconds)s timeout; result discarded")
                }
            } catch {
                if once.claim() { cont.resume(throwing: error) }
            }
        }
        Task {
            try? await Task.sleep(nanoseconds: UInt64(max(0, seconds)) * 1_000_000_000)
            opTask.cancel()  // cooperative: async operations abort, blocking calls run out on their own
            if once.claim() {
                cont.resume(throwing: BTError.timeout)
            }
        }
    }
}
