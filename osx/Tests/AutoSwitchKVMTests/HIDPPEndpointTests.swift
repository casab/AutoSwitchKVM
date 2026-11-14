import XCTest

@testable import AutoSwitchKVM

/// The HID++ backend beyond Logitech (agent-notes/2026-11-14-osx-zmk-hidpp-proposal.md): which
/// HID devices it may talk to, the name it writes, and how such devices are described.
@MainActor
final class HIDPPEndpointTests: XCTestCase {
    private func isEndpoint(_ vendor: Int, _ pairs: [(Int, Int)], maxOut: Int = 20) -> Bool {
        LogitechHostSwitcher.isHIDPPEndpoint(
            vendorID: vendor, usagePairs: pairs.map { (page: $0.0, usage: $0.1) }, maxOutput: maxOut)
    }

    func testLogitechDevicesWithAVendorCollectionQualify() {
        // K380 over Classic: keyboard + consumer + HID++ (0xFF00) in one device.
        XCTAssertTrue(isEndpoint(0x046D, [(0x01, 0x06), (0x0C, 0x01), (0xFF00, 0x01)]))
        // Logitech BLE layout.
        XCTAssertTrue(isEndpoint(0x046D, [(0xFF43, 0x0202)]))
        XCTAssertFalse(isEndpoint(0x046D, [(0x01, 0x06)]), "no vendor collection")
        XCTAssertFalse(isEndpoint(0x046D, [(0xFF00, 0x01)], maxOut: 8), "no long report")
    }

    func testOtherVendorsOnlyWithLogitechsHIDPPCollection() {
        // ZMK module v2 (its own vendor ID).
        XCTAssertTrue(isEndpoint(0x1D50, [(0xFF43, 0x0202)]))
        // The Magic Trackpad's own vendor collection must never be sent HID++.
        XCTAssertFalse(isEndpoint(0x004C, [(0x01, 0x02), (0xFF00, 0x0C)], maxOut: 64))
        // The ZMK module v1 raw HID interface is for the other backend.
        XCTAssertFalse(isEndpoint(0x1D50, [(0xFF60, 0x61)], maxOut: 32))
        XCTAssertFalse(isEndpoint(0x1D50, [(0xFF43, 0x0001)]), "other usage on the page")
    }

    func testNamesAreCutAtACharacterBoundaryAndSentIn14BytePieces() {
        let name = "Engin’s MacBook Pro"  // ’ is 3 bytes: 21 bytes in all
        XCTAssertEqual(LogitechHostSwitcher.encodeName(name, maxBytes: 32), Array(name.utf8))
        XCTAssertEqual(LogitechHostSwitcher.encodeName(name, maxBytes: 7), Array("Engin".utf8), "’ doesn't fit")
        XCTAssertEqual(LogitechHostSwitcher.encodeName("  PC \n", maxBytes: 32), Array("PC".utf8))

        let pieces = LogitechHostSwitcher.namePieces(Array(name.utf8))
        XCTAssertEqual(pieces.map { $0.0 }, [0, 14])
        XCTAssertEqual(pieces.map { $0.1.count }, [14, 7])
        XCTAssertEqual(pieces.flatMap { $0.1 }, Array(name.utf8))
        XCTAssertTrue(LogitechHostSwitcher.namePieces([]).isEmpty)
        XCTAssertFalse(LogitechHostSwitcher.computerName().isEmpty)
    }

    func testDescriptionFollowsTheProtocolAndVendor() {
        let hidpp = HostChannels(
            count: 5, current: 1, marks: [0, 0, 0, 0, 0], paired: [true, true, false, false, false])
        XCTAssertEqual(hidpp.noun, "Channel", "pairing known over HID++ is still a channel")
        XCTAssertEqual(HandoffAssessment.channelSwitch(hidpp, vendorID: 0x046D), .channelSwitch)
        let zmkV2 = HandoffAssessment.channelSwitch(hidpp, vendorID: 0x1D50)
        XCTAssertEqual(zmkV2.type, .channelSwitch)
        XCTAssertTrue(zmkV2.reason.contains("HID++"), zmkV2.reason)

        var v1 = hidpp
        v1.noun = "Profile"
        XCTAssertTrue(HandoffAssessment.channelSwitch(v1, vendorID: 0x1D50).reason.contains("profiles"))
    }

    func testThisComputersChannelFromTheStoredNames() {
        let me = "Engin’s MacBook Pro"
        // The K380: named by the device itself, the PC twice (a stale pairing).
        XCTAssertEqual(
            HostChannels.ownChannel(
                names: ["ENGIN-DESKTOP", me, "ENGIN-DESKTOP"], paired: [true, true, true], myName: me), 1)
        // The Reviung41 before the Mac wrote its name: the only paired channel without a name.
        XCTAssertEqual(
            HostChannels.ownChannel(
                names: ["engin-desktop", nil, nil, nil, nil], paired: [true, true, false, false, false], myName: me), 1)
        // A name cut to the device's maximum still matches.
        XCTAssertEqual(HostChannels.ownChannel(names: ["PC", "Engin’s MacBo"], paired: nil, myName: me), 1)
        // Can't tell: no names, two unnamed paired channels, or the name twice.
        XCTAssertNil(HostChannels.ownChannel(names: nil, paired: [true, true], myName: me))
        XCTAssertNil(HostChannels.ownChannel(names: [nil, nil, "PC"], paired: [true, true, true], myName: me))
        XCTAssertNil(HostChannels.ownChannel(names: [me, me], paired: [true, true], myName: me))
        XCTAssertFalse(HostChannels.nameMatches("Engin", me), "too short to be a cut-down copy")
    }
}
