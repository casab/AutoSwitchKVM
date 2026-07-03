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
        XCTAssertEqual(reloaded.config.schemaVersion, AppConfig.currentSchemaVersion)
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

    /// A corrupt config falls back to defaults, but the unreadable original must be preserved —
    /// the next debounced save would otherwise silently destroy the user's profiles.
    func testCorruptConfigIsBackedUpBeforeDefaulting() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try Data("{this is not json".utf8).write(to: dir.appendingPathComponent("config.json"))

        let store = ConfigStore(directory: dir)

        XCTAssertEqual(store.config.profiles.count, 1)  // defaults in use
        XCTAssertTrue(
            FileManager.default.fileExists(atPath: dir.appendingPathComponent("config.json.bad").path),
            "the corrupt original must be backed up for recovery")
    }
}
