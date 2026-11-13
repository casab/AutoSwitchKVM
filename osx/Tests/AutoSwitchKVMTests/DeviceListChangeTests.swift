import XCTest

@testable import AutoSwitchKVM

/// Spec §3 Evaluate step 6 (mirrors windows/tests DeviceListChangeTests): a device list edit is
/// not a seed. An added or re-enabled device connects on its own while this Mac is selected; the
/// other devices are left alone; a removed device is forgotten.
@MainActor
final class DeviceListChangeTests: XCTestCase {
    private let addrA = "aa-aa-aa-aa-aa-01"
    private let addrB = "bb-bb-bb-bb-bb-02"

    private struct Fixture {
        let engine: SelectionEngine
        let bt: MockBluetoothController
        let store: ConfigStore
    }

    private func make() -> Fixture {
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigStore(directory: tmp)
        store.config.source = USBSource(name: "Hub", vendorID: 0x05E3, productIDs: [0x0626])
        store.config.devices = [BTDevice(name: "Trackpad", address: addrA, managePairing: true, connectDelayMs: 0)]
        let bt = MockBluetoothController()
        bt.pairLeavesLinkUp = true
        let engine = SelectionEngine(store: store, usb: MockUSBMonitor(), bt: bt)
        engine.sleepHook = { _ in }
        return Fixture(engine: engine, bt: bt, store: store)
    }

    private func snapshot(_ f: Fixture) -> SelectionEngine.DeviceListSnapshot {
        .init(profileID: f.store.config.activeProfileID, devices: f.store.config.devices)
    }

    /// The KVM is here and the trackpad is connected.
    private func selected(_ f: Fixture) async {
        f.engine.handleUSB((vendorID: 0x05E3, productID: 0x0626, added: true))
        await f.engine.evaluateNow()
        XCTAssertTrue(f.bt.connectedAddrs.contains(addrA))
    }

    func testAnAddedDeviceConnectsAndTheOthersAreLeftAlone() async {
        let f = make()
        await selected(f)
        let before = snapshot(f)
        let callsBefore = f.bt.calls.count

        f.store.config.devices.append(BTDevice(name: "Keyboard", address: addrB, connectDelayMs: 0))
        await f.engine.deviceListChanged(from: before, to: snapshot(f))

        let calls = Array(f.bt.calls[callsBefore...])
        XCTAssertTrue(f.bt.connectedAddrs.contains(addrB), "the added device connected: \(calls)")
        XCTAssertFalse(calls.contains { $0.hasSuffix(addrA) }, "the trackpad isn't touched again: \(calls)")
    }

    func testAReEnabledDeviceConnects() async {
        let f = make()
        f.store.config.devices.append(BTDevice(name: "Keyboard", address: addrB, enabled: false, connectDelayMs: 0))
        await selected(f)
        XCTAssertFalse(f.bt.connectedAddrs.contains(addrB), "disabled: not connected by the run")

        let before = snapshot(f)
        f.store.config.devices[1].enabled = true
        await f.engine.deviceListChanged(from: before, to: snapshot(f))
        XCTAssertTrue(f.bt.connectedAddrs.contains(addrB))
    }

    func testNothingConnectsWhileTheKVMIsElsewhereOrPaused() async {
        let f = make()  // never selected
        var before = snapshot(f)
        f.store.config.devices.append(BTDevice(name: "Keyboard", address: addrB, connectDelayMs: 0))
        await f.engine.deviceListChanged(from: before, to: snapshot(f))
        XCTAssertEqual(f.bt.calls, [], "not selected: nothing happens")

        await selected(f)
        f.store.config.paused = true
        let callsBefore = f.bt.calls.count
        before = snapshot(f)
        f.store.config.devices.append(BTDevice(name: "Mouse", address: "cc-cc-cc-cc-cc-03", connectDelayMs: 0))
        await f.engine.deviceListChanged(from: before, to: snapshot(f))
        XCTAssertEqual(f.bt.calls.count, callsBefore, "paused: nothing happens")
    }

    /// A profile switch re-seeds the engine, which connects everything itself.
    func testAProfileSwitchIsLeftToTheSeed() async {
        let f = make()
        await selected(f)
        let callsBefore = f.bt.calls.count
        let other = SelectionEngine.DeviceListSnapshot(
            profileID: UUID(), devices: [BTDevice(name: "Keyboard", address: addrB, connectDelayMs: 0)])
        await f.engine.deviceListChanged(from: snapshot(f), to: other)
        XCTAssertEqual(f.bt.calls.count, callsBefore)
    }

    func testARemovedDeviceIsForgottenAndLeftAsItIs() async {
        let f = make()
        await selected(f)
        let id = f.store.config.devices[0].id
        XCTAssertEqual(f.engine.statuses[id], .connected)
        let before = snapshot(f)
        let callsBefore = f.bt.calls.count

        f.store.config.devices.removeAll()
        await f.engine.deviceListChanged(from: before, to: snapshot(f))
        XCTAssertNil(f.engine.statuses[id], "its status is dropped")
        XCTAssertEqual(f.bt.calls.count, callsBefore, "no disconnect or unpair")
        XCTAssertTrue(f.bt.connectedAddrs.contains(addrA))
    }

    /// End to end: editing the config's device list reaches the engine through the debounced
    /// subscription, like an edit in Settings.
    func testEditingTheConfigConnectsTheAddedDevice() async throws {
        let f = make()
        await selected(f)
        f.store.config.devices.append(BTDevice(name: "Keyboard", address: addrB, connectDelayMs: 0))
        let deadline = Date().addingTimeInterval(3)
        while !f.bt.connectedAddrs.contains(addrB), Date() < deadline {
            try await Task.sleep(nanoseconds: 50_000_000)
        }
        XCTAssertTrue(f.bt.connectedAddrs.contains(addrB))
    }
}
