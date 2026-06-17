import XCTest

@testable import AutoSwitchKVM

@MainActor
final class ConfigStoreTests: XCTestCase {

    func testRoundTripPersistsConfig() {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)

        let store = ConfigStore(directory: dir)
        store.config.source = USBSource(name: "Hub", vendorID: 0x05E3, productIDs: [0x0626, 0x0610])
        store.config.devices = [
            BTDevice(
                name: "Trackpad", address: "3c-50-02-bf-22-45",
                enabled: true, managePairing: true)
        ]
        store.config.debounceMs = 800
        store.config.dockAutoHide = true
        store.saveNow()

        let reloaded = ConfigStore(directory: dir)
        XCTAssertEqual(reloaded.config.source?.name, "Hub")
        XCTAssertEqual(reloaded.config.source?.productIDs, [0x0626, 0x0610])
        XCTAssertEqual(reloaded.config.devices.count, 1)
        XCTAssertEqual(reloaded.config.devices.first?.address, "3c-50-02-bf-22-45")
        XCTAssertEqual(reloaded.config.devices.first?.managePairing, true)
        XCTAssertEqual(reloaded.config.debounceMs, 800)
        XCTAssertTrue(reloaded.config.dockAutoHide)
    }

    func testDefaultsWhenNoFile() {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigStore(directory: dir)
        XCTAssertNil(store.config.source)
        XCTAssertTrue(store.config.devices.isEmpty)
        XCTAssertEqual(store.config.debounceMs, AppConfig.default.debounceMs)
    }
}
