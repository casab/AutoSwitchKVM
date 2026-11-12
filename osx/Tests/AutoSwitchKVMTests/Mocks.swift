import Combine
import Foundation

@testable import AutoSwitchKVM

/// In-memory USB monitor for tests — drive `events` directly to simulate attach/detach.
@MainActor
final class MockUSBMonitor: USBMonitoring {
    var attachedDevices: [USBDeviceInfo] = []
    let events = PassthroughSubject<(vendorID: UInt16, productID: UInt16, added: Bool), Never>()
    func refreshAttached() {}
}

/// In-memory Bluetooth controller for tests. Records an ordered call log so ordering
/// (e.g. pair-before-connect) can be asserted. Reads (`isConnected`) are not recorded.
@MainActor
final class MockBluetoothController: BluetoothController {
    private(set) var connectedAddrs = Set<String>()
    private(set) var pairedAddrs = Set<String>()
    private(set) var calls: [String] = []

    /// Awaited at the start of connect/disconnect/pair — lets tests suspend a call mid-flight
    /// to interleave engine runs at a realistic suspension point.
    var beforeConnect: (@MainActor () async -> Void)?
    var beforeDisconnect: (@MainActor () async -> Void)?
    var beforePair: (@MainActor () async -> Void)?

    /// Persistent failures: when set, the call is recorded, then throws without changing state.
    var connectError: Error?
    var disconnectError: Error?
    var unpairError: Error?

    /// One-shot failures, consumed in order before the persistent ones (e.g. "busy twice, then OK").
    var connectErrors: [Error] = []
    var pairErrors: [Error] = []
    var unpairErrors: [Error] = []

    /// Invoked after a successful unpair — lets tests model state that a fresh bond repairs
    /// (e.g. clear `connectError` to simulate a dead link key being replaced).
    var afterUnpair: (@MainActor () -> Void)?

    private func norm(_ a: String) -> String {
        a.replacingOccurrences(of: ":", with: "-").lowercased()
    }

    private func nextError(_ queue: inout [Error], _ persistent: Error?) -> Error? {
        if !queue.isEmpty { return queue.removeFirst() }
        return persistent
    }

    func isConnected(_ address: String) async -> Bool { connectedAddrs.contains(norm(address)) }

    func connect(_ address: String) async throws {
        if let hook = beforeConnect { await hook() }
        calls.append("connect:\(norm(address))")
        if let error = nextError(&connectErrors, connectError) { throw error }
        connectedAddrs.insert(norm(address))
    }

    func disconnect(_ address: String) async throws {
        if let hook = beforeDisconnect { await hook() }
        calls.append("disconnect:\(norm(address))")
        if let error = disconnectError { throw error }
        connectedAddrs.remove(norm(address))
    }

    func pair(_ address: String) async throws {
        if let hook = beforePair { await hook() }
        calls.append("pair:\(norm(address))")
        if let error = nextError(&pairErrors, nil) { throw error }
        pairedAddrs.insert(norm(address))
    }

    /// Like `-[IOBluetoothDevice remove]`, removing the bond also drops the link.
    func unpair(_ address: String) async throws {
        calls.append("unpair:\(norm(address))")
        if let error = nextError(&unpairErrors, unpairError) { throw error }
        pairedAddrs.remove(norm(address))
        connectedAddrs.remove(norm(address))
        afterUnpair?()
    }

    func pairedDevices() async -> [PairedDeviceInfo] {
        pairedAddrs.map { PairedDeviceInfo(name: $0, address: $0) }
    }

    var powered = true
    func isPoweredOn() async -> Bool? { powered }

    private(set) var monitoringOnChange: ((String, Bool) -> Void)?

    func startMonitoring(addresses: [String], onChange: @escaping (String, Bool) -> Void) {
        monitoringOnChange = onChange
    }

    func stopMonitoring() { monitoringOnChange = nil }

    /// Simulate an IOBluetooth connect/disconnect notification for a monitored device.
    func fireConnectionChange(_ address: String, _ connected: Bool) {
        monitoringOnChange?(norm(address), connected)
    }

    /// Simulate a passive drop: the device disconnects on its own and the notification fires.
    func simulateConnectionDrop(_ address: String) {
        connectedAddrs.remove(norm(address))
        monitoringOnChange?(norm(address), false)
    }

    /// Test setup: the device is already bonded/connected (not recorded in `calls`).
    func preset(paired: Bool = false, connected: Bool = false, _ address: String) {
        if paired { pairedAddrs.insert(norm(address)) }
        if connected { connectedAddrs.insert(norm(address)) }
    }
}

/// One-shot gate a mock hook can suspend on, released explicitly by the test.
@MainActor
final class Gate {
    private var continuation: CheckedContinuation<Void, Never>?
    private var armed = true
    private(set) var reached = false

    /// Use as a mock hook: suspends the first caller until `open()`; later callers pass through.
    func hook() async {
        guard armed else { return }
        armed = false
        reached = true
        await withCheckedContinuation { continuation = $0 }
    }

    func open() {
        continuation?.resume()
        continuation = nil
    }
}
