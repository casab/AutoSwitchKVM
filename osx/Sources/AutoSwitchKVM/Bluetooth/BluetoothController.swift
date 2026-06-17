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

/// Run an async operation with a timeout (seconds). Throws `BTError.timeout` if it overruns.
func withTimeout<T: Sendable>(_ seconds: Int, _ operation: @escaping @Sendable () async throws -> T) async throws -> T {
    try await withThrowingTaskGroup(of: T.self) { group in
        group.addTask { try await operation() }
        group.addTask {
            try await Task.sleep(nanoseconds: UInt64(seconds) * 1_000_000_000)
            throw BTError.timeout
        }
        let result = try await group.next()!
        group.cancelAll()
        return result
    }
}
