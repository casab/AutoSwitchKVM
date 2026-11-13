import XCTest

@testable import AutoSwitchKVM

/// Spec §5a classification. Same cases as windows/tests HandoffCatalogTests (the user's devices,
/// 2026-11-13), plus the macOS identity source (system_profiler JSON).
final class HandoffCatalogTests: XCTestCase {
    private func c(
        _ name: String, le: Bool = false, _ vendor: UInt16?, _ product: UInt16? = nil, kind: DeviceKind = .unknown
    ) -> HandoffAssessment {
        HandoffCatalog.classify(
            DeviceIdentity(name: name, isLowEnergy: le, vendorID: vendor, productID: product, kind: kind))
    }

    func testMagicTrackpadHandsOffByPairing() {
        let a = c("Engin’s Magic Trackpad", 0x004C, 0x0324, kind: .input)
        XCTAssertEqual(a.type, .pairingHandoff)
        XCTAssertEqual(a.confidence, .known)
        XCTAssertTrue(a.defaultManagePairing(isLowEnergy: false))
        // No vendor known (not paired here right now): the name is enough.
        XCTAssertEqual(c("Magic Trackpad", nil).type, .pairingHandoff)
    }

    func testZMKKeyboardPicksTheComputerItself() {
        let a = c("Reviung41", le: true, 0x1D50, 0x615E, kind: .input)
        XCTAssertEqual(a.type, .devicePicksHost)
        XCTAssertEqual(a.confidence, .known)
        XCTAssertFalse(a.defaultManagePairing(isLowEnergy: true))
    }

    func testConsoleControllersRePairByHand() {
        for (name, le, vendor) in [
            ("Xbox Wireless Controller", true, UInt16(0x045E)),
            ("Pro Controller", false, 0x057E),
            ("DualSense Wireless Controller", false, 0x054C),
        ] {
            let a = c(name, le: le, vendor, kind: .gameController)
            XCTAssertEqual(a.type, .repairByHand, name)
            XCTAssertEqual(a.confidence, .known, name)
            XCTAssertFalse(a.defaultManagePairing(isLowEnergy: le), name)
        }
        XCTAssertEqual(c("8BitDo Gamepad", nil, kind: .gameController).confidence, .likely)
    }

    func testLogitechKeyboardIsLikelyEasySwitch() {
        let a = c("Keyboard K380", 0x046D, 0xB342, kind: .input)
        XCTAssertEqual(a.type, .devicePicksHost)
        XCTAssertEqual(a.confidence, .likely)
    }

    func testAudioDevicesJoinWhicheverComputerConnects() {
        XCTAssertEqual(c("WH-1000XM5", 0x054C, kind: .audio).type, .hostConnects)
        XCTAssertEqual(c("AirPods Pro", 0x004C, kind: .audio).type, .hostConnects)
        XCTAssertFalse(c("WF-1000XM6", 0x054C, kind: .audio).defaultManagePairing(isLowEnergy: false))
    }

    func testUnknownDevicesAskForATestAndKeepTheTransportDefault() {
        let classic = c("Some Mouse", nil)
        XCTAssertEqual(classic.type, .unknown)
        XCTAssertTrue(classic.defaultManagePairing(isLowEnergy: false))
        XCTAssertFalse(classic.defaultManagePairing(isLowEnergy: true))
        XCTAssertEqual(c("BLE Keyboard", le: true, nil, kind: .input).type, .devicePicksHost)
    }

    func testChannelSwitchIsKnownAndKeepsPairingOff() {
        XCTAssertEqual(HandoffAssessment.channelSwitch.type, .channelSwitch)
        XCTAssertFalse(HandoffAssessment.channelSwitch.defaultManagePairing(isLowEnergy: false))
    }

    // MARK: - Wording

    func testManagePairingHintOnlyWhenTheSettingWorksAgainstTheType() {
        let trackpad = c("Magic Trackpad", 0x004C, kind: .input)
        XCTAssertNil(HandoffText.managePairingHint(trackpad, managePairing: true))
        XCTAssertNotNil(HandoffText.managePairingHint(trackpad, managePairing: false))
        let zmk = c("Reviung41", 0x1D50, 0x615E)
        XCTAssertNil(HandoffText.managePairingHint(zmk, managePairing: false))
        XCTAssertNotNil(HandoffText.managePairingHint(zmk, managePairing: true))
        XCTAssertNil(HandoffText.managePairingHint(c("Gizmo", nil), managePairing: true))
        XCTAssertNil(HandoffText.managePairingHint(c("Gizmo", nil), managePairing: false))
    }

    func testMacCanSwitchPairingConnectionAndChannelTypes() {
        XCTAssertTrue(HandoffText.canSwitch(.pairingHandoff))
        XCTAssertTrue(HandoffText.canSwitch(.hostConnects))  // Mac: disconnect without unpairing
        XCTAssertTrue(HandoffText.canSwitch(.channelSwitch))
        XCTAssertFalse(HandoffText.canSwitch(.devicePicksHost))
        XCTAssertFalse(HandoffText.canSwitch(.repairByHand))
        XCTAssertFalse(HandoffText.canSwitch(.unknown))
    }

    // MARK: - system_profiler identities

    /// Shaped like `system_profiler SPBluetoothDataType -json` on the user's Mac (2026-11-13),
    /// with made-up addresses.
    private let sample = Data(
        """
        {"SPBluetoothDataType": [{
          "controller_properties": {"controller_vendorID": "0x004C (Apple)"},
          "device_connected": [
            {"Keyboard K380": {"device_address": "AA:BB:CC:00:00:01", "device_minorType": "Keyboard",
                               "device_productID": "0xB342", "device_vendorID": "0x046D"}}
          ],
          "device_not_connected": [
            {"Phone": {"device_address": "AA:BB:CC:00:00:02", "device_rssi": "-52"}},
            {"Reviung41": {"device_address": "AA:BB:CC:00:00:03", "device_minorType": "Keyboard",
                           "device_productID": "0x615E", "device_vendorID": "0x1D50"}},
            {"WF-1000XM6": {"device_address": "AA:BB:CC:00:00:04", "device_minorType": "Headset",
                            "device_productID": "0x0F86", "device_vendorID": "0x054C"}},
            {"Xbox Wireless Controller": {"device_address": "AA:BB:CC:00:00:05", "device_minorType": "Gamepad",
                                          "device_productID": "0x0B13", "device_vendorID": "0x045E"}}
          ]
        }]}
        """.utf8)

    func testParsesSystemProfilerIdentities() {
        let ids = DeviceIdentityReader.parse(sample)
        XCTAssertEqual(ids.count, 5)
        let k380 = ids["AABBCC000001"]
        XCTAssertEqual(k380?.name, "Keyboard K380")
        XCTAssertEqual(k380?.vendorID, 0x046D)
        XCTAssertEqual(k380?.productID, 0xB342)
        XCTAssertEqual(k380?.kind, .input)
        XCTAssertEqual(ids["AABBCC000002"]?.vendorID, nil)
        XCTAssertEqual(ids["AABBCC000002"]?.kind, .unknown)

        let types = ids.mapValues { HandoffCatalog.classify($0).type }
        XCTAssertEqual(types["AABBCC000001"], .devicePicksHost)
        XCTAssertEqual(types["AABBCC000002"], .unknown)
        XCTAssertEqual(types["AABBCC000003"], .devicePicksHost)
        XCTAssertEqual(types["AABBCC000004"], .hostConnects)
        XCTAssertEqual(types["AABBCC000005"], .repairByHand)
    }

    func testParsesHexAndMinorTypesAndClassOfDevice() {
        XCTAssertEqual(DeviceIdentityReader.hex("0x004C (Apple)"), 0x004C)
        XCTAssertEqual(DeviceIdentityReader.hex("0x046D"), 0x046D)
        XCTAssertNil(DeviceIdentityReader.hex("n/a"))
        XCTAssertNil(DeviceIdentityReader.hex(nil))
        XCTAssertEqual(DeviceIdentityReader.kind(minorType: "Gamepad"), .gameController)
        XCTAssertEqual(DeviceIdentityReader.kind(minorType: "Mouse"), .input)
        XCTAssertEqual(DeviceIdentityReader.kind(minorType: "Headphones"), .audio)
        XCTAssertEqual(DeviceIdentityReader.kind(minorType: nil), .unknown)
        XCTAssertEqual(DeviceIdentityReader.kind(major: 0x05, minor: 0x10), .input)  // keyboard
        XCTAssertEqual(DeviceIdentityReader.kind(major: 0x05, minor: 0x02), .gameController)  // gamepad
        XCTAssertEqual(DeviceIdentityReader.kind(major: 0x04, minor: 0x01), .audio)
        XCTAssertEqual(DeviceIdentityReader.kind(major: 0x01, minor: 0x00), .unknown)
        XCTAssertTrue(DeviceIdentityReader.parse(Data("not json".utf8)).isEmpty)
    }
}
