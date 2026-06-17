import Foundation
import Combine
@testable import AutoSwitchKVM

/// In-memory USB monitor for tests — drive `events` directly to simulate attach/detach.
@MainActor
final class MockUSBMonitor: USBMonitoring {
    var attachedDevices: [USBDeviceInfo] = []
    let events = PassthroughSubject<(vendorID: UInt16, productID: UInt16, added: Bool), Never>()
    func refreshAttached() {}
}

/// In-memory Bluetooth controller for tests. Records an ordered call log so ordering
/// (e.g. pair-before-connect) can be asserted.
@MainActor
final class MockBluetoothController: BluetoothController {
    private(set) var connectedAddrs = Set<String>()
    private(set) var pairedAddrs = Set<String>()
    private(set) var calls: [String] = []

    private func norm(_ a: String) -> String {
        a.replacingOccurrences(of: ":", with: "-").lowercased()
    }

    func isConnected(_ address: String) async -> Bool { connectedAddrs.contains(norm(address)) }

    func connect(_ address: String) async throws {
        calls.append("connect:\(norm(address))")
        connectedAddrs.insert(norm(address))
    }

    func disconnect(_ address: String) async throws {
        calls.append("disconnect:\(norm(address))")
        connectedAddrs.remove(norm(address))
    }

    func pair(_ address: String) async throws {
        calls.append("pair:\(norm(address))")
        pairedAddrs.insert(norm(address))
    }

    func unpair(_ address: String) async throws {
        calls.append("unpair:\(norm(address))")
        pairedAddrs.remove(norm(address))
    }

    func pairedDevices() async -> [PairedDeviceInfo] {
        pairedAddrs.map { PairedDeviceInfo(name: $0, address: $0) }
    }

    var powered = true
    func isPoweredOn() async -> Bool? { powered }

    func startMonitoring(addresses: [String], onChange: @escaping (String, Bool) -> Void) {}
    func stopMonitoring() {}
}
