import XCTest

@testable import AutoSwitchKVM

final class ModelsTests: XCTestCase {

    func testAddressNormalization() {
        let colon = BTDevice(name: "x", address: "3C:50:02:BF:22:45")
        XCTAssertEqual(colon.normalizedAddress, "3c-50-02-bf-22-45")

        let dash = BTDevice(name: "x", address: "3C-50-02-BF-22-45")
        XCTAssertEqual(dash.normalizedAddress, "3c-50-02-bf-22-45")
    }

    func testSourceDisplayVidPid() {
        let source = USBSource(name: "Hub", vendorID: 0x05E3, productIDs: [0x0626, 0x0610])
        // Product IDs are sorted in the display string.
        XCTAssertEqual(source.displayVidPid, "0x05E3 : 0x0610, 0x0626")
    }

    func testDeviceStatusLabels() {
        XCTAssertEqual(DeviceStatus.connected.label, "Connected")
        XCTAssertEqual(DeviceStatus.error("boom").label, "Error: boom")
    }

    /// Backward-compatible decode: keys missing from older configs fall back to defaults
    /// instead of failing the whole decode.
    func testMissingKeysDecodeToDefaults() throws {
        let cfg = try JSONDecoder().decode(AppConfig.self, from: Data("{}".utf8))
        XCTAssertEqual(cfg.debounceMs, 400)
        XCTAssertEqual(cfg.arrivalDebounceMs, 400)
        XCTAssertEqual(cfg.btCallTimeoutSecs, 15)
        XCTAssertEqual(cfg.postUnpairSettleMs, 2000)
        XCTAssertEqual(cfg.logLevel, .info)
        XCTAssertEqual(cfg.profiles.count, 1)  // empty → one Default profile
        XCTAssertEqual(cfg.hotkeyPause, .defaultPause)  // absent → the default combo

        let dev = try JSONDecoder().decode(
            BTDevice.self, from: Data(#"{"name":"x","address":"aa-bb"}"#.utf8))
        XCTAssertTrue(dev.enabled)
        XCTAssertFalse(dev.managePairing)
        XCTAssertEqual(dev.connectDelayMs, 0)
    }

    /// A config exported on Windows stores hotkeys as `modifiers` (Windows virtual-key space);
    /// importing it on macOS must not fail wholesale, and the foreign shortcut must fall back to
    /// the platform default rather than registering a wrong-key combo.
    func testWindowsExportedHotkeyFallsBackToDefaultOnDecode() throws {
        let json = """
            {
              "profiles": [
                {"id": "6F9619FF-8B86-D011-B42D-00C04FC964FF", "name": "Default", "devices": []}
              ],
              "activeProfileID": "6F9619FF-8B86-D011-B42D-00C04FC964FF",
              "paused": true,
              "hotkeyPause": {"keyCode": 80, "modifiers": 3, "display": "Ctrl+Alt+P"},
              "hotkeyConnectAll": null
            }
            """
        let cfg = try JSONDecoder().decode(AppConfig.self, from: Data(json.utf8))

        XCTAssertTrue(cfg.paused, "the import itself must succeed")
        XCTAssertEqual(cfg.hotkeyPause, .defaultPause, "foreign (Windows) hotkey → platform default")
        XCTAssertNil(cfg.hotkeyConnectAll, "explicit null stays cleared")
    }
}
