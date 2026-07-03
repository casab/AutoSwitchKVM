import XCTest

@testable import AutoSwitchKVM

/// Cross-platform config parity guard. `testdata/config-golden.json` at the repo root is the
/// canonical fixture both platforms' suites decode, round-trip, and key-compare — any key
/// rename or dropped field on either side fails CI. Hotkeys in the fixture are explicit null
/// (cleared): they are the one deliberately platform-specific part of the schema.
@MainActor
final class GoldenConfigTests: XCTestCase {

    private var goldenURL: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // AutoSwitchKVMTests
            .deletingLastPathComponent()  // Tests
            .deletingLastPathComponent()  // osx
            .deletingLastPathComponent()  // repo root
            .appendingPathComponent("testdata")
            .appendingPathComponent("config-golden.json")
    }

    func testGoldenConfigDecodesRoundTripsAndKeepsKeyParity() throws {
        let data = try Data(contentsOf: goldenURL)
        let cfg = try JSONDecoder().decode(AppConfig.self, from: data)

        XCTAssertEqual(cfg.schemaVersion, 1)
        XCTAssertEqual(cfg.profiles.count, 2)
        XCTAssertEqual(cfg.activeProfileName, "Desk")
        XCTAssertEqual(cfg.source?.name, "Desk KVM")
        XCTAssertEqual(cfg.source?.vendorID, 0x05E3)
        XCTAssertEqual(cfg.source?.productIDs, [0x0610, 0x0626])
        XCTAssertEqual(cfg.devices.count, 1)
        XCTAssertEqual(cfg.devices.first?.name, "Magic Trackpad")
        XCTAssertEqual(cfg.devices.first?.address, "3c-50-02-bf-22-45")
        XCTAssertEqual(cfg.devices.first?.managePairing, true)
        XCTAssertEqual(cfg.devices.first?.connectDelayMs, 250)
        XCTAssertEqual(cfg.debounceMs, 900)
        XCTAssertEqual(cfg.arrivalDebounceMs, 300)
        XCTAssertEqual(cfg.connectRetryMax, 4)
        XCTAssertEqual(cfg.connectRetrySecs, 3)
        XCTAssertEqual(cfg.btCallTimeoutSecs, 25)
        XCTAssertTrue(cfg.showNotifications)
        XCTAssertTrue(cfg.notifyUnexpectedDisconnect)
        XCTAssertTrue(cfg.launchAtLogin)
        XCTAssertFalse(cfg.paused)
        XCTAssertTrue(cfg.dockAutoHide)
        XCTAssertFalse(cfg.globalHotkeysEnabled)
        XCTAssertNil(cfg.hotkeyPause)  // explicit null = cleared, not defaulted

        // Round-trip: nothing lost or renamed.
        let reencoded = try JSONEncoder().encode(cfg)
        let again = try JSONDecoder().decode(AppConfig.self, from: reencoded)
        XCTAssertEqual(again.btCallTimeoutSecs, 25)
        XCTAssertEqual(again.devices.first?.address, "3c-50-02-bf-22-45")
        XCTAssertEqual(again.activeProfileID, cfg.activeProfileID)
        XCTAssertNil(again.hotkeyPause)

        // Top-level key parity with the canonical fixture.
        let emitted = try XCTUnwrap(JSONSerialization.jsonObject(with: reencoded) as? [String: Any])
        let fixture = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(Set(emitted.keys), Set(fixture.keys))
    }
}
