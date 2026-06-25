import XCTest

@testable import AutoSwitchKVM

final class ProfilesTests: XCTestCase {

    /// A pre-profiles config (top-level source/devices) should migrate into one "Default" profile.
    func testLegacyConfigMigratesToDefaultProfile() throws {
        let json = """
            {
              "source": { "name": "Old KVM", "vendorID": 1507, "productIDs": [1574, 1552] },
              "devices": [
                { "id": "11111111-1111-1111-1111-111111111111", "name": "Trackpad",
                  "address": "3c-50-02-bf-22-45", "enabled": true, "managePairing": true }
              ],
              "debounceMs": 900
            }
            """.data(using: .utf8)!

        let cfg = try JSONDecoder().decode(AppConfig.self, from: json)
        XCTAssertEqual(cfg.schemaVersion, AppConfig.currentSchemaVersion)
        XCTAssertEqual(cfg.profiles.count, 1)
        XCTAssertEqual(cfg.profiles.first?.name, "Default")
        XCTAssertEqual(cfg.source?.name, "Old KVM")
        XCTAssertEqual(cfg.source?.productIDs, [1574, 1552])
        XCTAssertEqual(cfg.devices.count, 1)
        XCTAssertEqual(cfg.devices.first?.managePairing, true)
        XCTAssertEqual(cfg.debounceMs, 900)
        XCTAssertEqual(cfg.activeProfileID, cfg.profiles.first?.id)
    }

    /// `source`/`devices` read and write the currently active profile.
    func testActiveAccessorsFollowActiveProfile() {
        var cfg = AppConfig()  // one "Default" profile
        cfg.source = USBSource(name: "Desk", vendorID: 3, productIDs: [4])

        let travel = Profile(
            name: "Travel",
            source: USBSource(name: "TravelHub", vendorID: 5, productIDs: [6]))
        cfg.profiles.append(travel)

        XCTAssertEqual(cfg.source?.name, "Desk")  // Default still active

        cfg.activeProfileID = travel.id
        XCTAssertEqual(cfg.source?.name, "TravelHub")

        // Editing through the accessor writes only the active (Travel) profile.
        cfg.devices = [BTDevice(name: "Mouse", address: "aa-bb")]
        XCTAssertEqual(cfg.profiles.first { $0.id == travel.id }?.devices.count, 1)
        XCTAssertEqual(cfg.profiles.first?.devices.count, 0)  // Default untouched
    }

    func testRoundTripPreservesProfiles() throws {
        var cfg = AppConfig()
        cfg.activeProfileName = "Desk"
        cfg.source = USBSource(name: "Hub", vendorID: 1, productIDs: [2])
        cfg.profiles.append(Profile(name: "Travel"))

        let data = try JSONEncoder().encode(cfg)
        let back = try JSONDecoder().decode(AppConfig.self, from: data)

        XCTAssertEqual(back.profiles.count, 2)
        XCTAssertEqual(back.activeProfileID, cfg.activeProfileID)
        XCTAssertEqual(back.source?.name, "Hub")
        XCTAssertEqual(back.profiles.map(\.name).sorted(), ["Desk", "Travel"])
    }
}
