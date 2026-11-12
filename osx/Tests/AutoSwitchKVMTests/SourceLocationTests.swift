import XCTest

@testable import AutoSwitchKVM

/// The source must be the KVM's *switching* hubs, not a permanent dock hub with the same IDs.
/// Measured on hardware 2026-11-12 (KVM on Windows vs on the Mac): the dock keeps Genesys
/// 0x0610 @ 0x00120000 and 0x0626 @ 0x00220000 attached permanently; only 0x0610 @ 0x00124000
/// and 0x0626 @ 0x00224000 come and go with the KVM. Matching by IDs alone made the Mac think it
/// was selected at startup while the KVM was on Windows.
@MainActor
final class SourceLocationTests: XCTestCase {
    private let vendor: UInt16 = 0x05E3
    private let addr = "3c-50-02-bf-22-45"
    private func hub(_ pid: UInt16, at location: UInt32) -> USBDeviceInfo {
        let name = pid == 0x0610 ? "USB2.1 Hub" : "USB3.1 Hub"
        return USBDeviceInfo(vendorID: vendor, productID: pid, name: name, locationID: location)
    }
    private var dockHub2: USBDeviceInfo { hub(0x0610, at: 0x0012_0000) }
    private var dockHub3: USBDeviceInfo { hub(0x0626, at: 0x0022_0000) }
    private var kvmHub2: USBDeviceInfo { hub(0x0610, at: 0x0012_4000) }
    private var kvmHub3: USBDeviceInfo { hub(0x0626, at: 0x0022_4000) }

    private func make(pinned: Bool) -> (SelectionEngine, MockBluetoothController, MockUSBMonitor) {
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigStore(directory: tmp)
        store.config.source = USBSource(
            name: "Desk KVM", vendorID: vendor, productIDs: [0x0610, 0x0626],
            locationIDs: pinned ? [0x0012_4000, 0x0022_4000] : nil)
        store.config.devices = [BTDevice(name: "Trackpad", address: addr, enabled: true)]
        let usb = MockUSBMonitor()
        let bt = MockBluetoothController()
        let engine = SelectionEngine(store: store, usb: usb, bt: bt)
        engine.sleepHook = { _ in }
        return (engine, bt, usb)
    }

    private func event(_ d: USBDeviceInfo, added: Bool) -> USBEvent {
        USBEvent(vendorID: d.vendorID, productID: d.productID, locationID: d.locationID, added: added)
    }

    /// The reported bug: app started while the KVM was on Windows.
    func testStartupWithOnlyTheDockHubsIsNotSelected() async {
        let (engine, bt, usb) = make(pinned: true)
        usb.attachedDevices = [dockHub2, dockHub3]

        engine.seed()
        await engine.evaluateNow()

        XCTAssertFalse(engine.selected, "dock hubs with the same IDs must not count as the KVM")
        XCTAssertTrue(bt.calls.isEmpty, "nothing may connect while the KVM is on the other host: \(bt.calls)")
    }

    func testKVMHubsAreTrackedByLocationNotByID() async {
        let (engine, bt, usb) = make(pinned: true)
        usb.attachedDevices = [dockHub2, dockHub3]
        engine.seed()
        await engine.evaluateNow()

        engine.handleUSB(event(kvmHub2, added: true))
        await engine.evaluateNow()
        XCTAssertTrue(engine.selected)
        XCTAssertTrue(bt.connectedAddrs.contains(addr))

        // The dock hub (same IDs) changing must not change presence.
        engine.handleUSB(event(dockHub2, added: false))
        await engine.evaluateNow()
        XCTAssertTrue(engine.selected, "a dock-hub event must not end the KVM's presence")

        engine.handleUSB(event(kvmHub2, added: false))
        await engine.evaluateNow()
        XCTAssertFalse(engine.selected)
        XCTAssertFalse(bt.connectedAddrs.contains(addr))
    }

    /// Configs without locations keep the original ID matching (documented, not desired).
    func testUnpinnedSourceKeepsMatchingByID() async {
        let (engine, _, usb) = make(pinned: false)
        usb.attachedDevices = [dockHub2]
        engine.seed()
        await engine.evaluateNow()
        XCTAssertTrue(engine.selected)
    }

    func testSourceBuiltFromDevicesPinsTheirLocations() {
        let pinned = USBSource.from(name: "Desk KVM", devices: [kvmHub2, kvmHub3])
        XCTAssertEqual(pinned?.locationIDs, [0x0012_4000, 0x0022_4000])
        XCTAssertEqual(pinned?.displayLocations, "@ 0x00124000, 0x00224000")

        let unknownLocation = USBDeviceInfo(vendorID: vendor, productID: 0x0610, name: "Hub")
        XCTAssertNil(USBSource.from(name: "x", devices: [unknownLocation])?.locationIDs)
    }

    func testLocationIDsAreOptionalInJSON() throws {
        let legacy = try JSONDecoder().decode(
            USBSource.self, from: Data(#"{"name":"KVM","vendorID":1507,"productIDs":[1552]}"#.utf8))
        XCTAssertNil(legacy.locationIDs)
        let unpinnedJSON = String(decoding: try JSONEncoder().encode(legacy), as: UTF8.self)
        XCTAssertFalse(unpinnedJSON.contains("locationIDs"), "nil must be omitted: \(unpinnedJSON)")

        let pinned = USBSource(name: "KVM", vendorID: 0x05E3, productIDs: [0x0610], locationIDs: [0x0012_4000])
        let back = try JSONDecoder().decode(USBSource.self, from: try JSONEncoder().encode(pinned))
        XCTAssertEqual(back.locationIDs, [0x0012_4000])
    }

    func testLearnerKeepsSameIDDevicesApartByLocation() {
        let usb = MockUSBMonitor()
        usb.attachedDevices = [dockHub2, dockHub3]
        let learner = SourceLearner(usb: usb)
        learner.start()

        usb.events.send(event(kvmHub2, added: true))
        usb.events.send(event(kvmHub3, added: true))
        usb.attachedDevices = [dockHub2, dockHub3, kvmHub2, kvmHub3]

        let candidates = learner.finish()
        XCTAssertEqual(Set(candidates.compactMap(\.locationID)), [0x0012_4000, 0x0022_4000])
        XCTAssertEqual(USBSource.from(name: "Desk KVM", devices: candidates)?.locationIDs, [0x0012_4000, 0x0022_4000])
    }
}
