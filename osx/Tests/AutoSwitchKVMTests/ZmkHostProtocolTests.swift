import XCTest

@testable import AutoSwitchKVM

/// The AutoSwitch ZMK host channel protocol (github.com/casab/zmk-autoswitch, protocol v1), the
/// composite switcher, and the engine's target rules for devices that report pairing. Mirrors
/// windows/tests ZmkHostProtocolTests.
@MainActor
final class ZmkHostProtocolTests: XCTestCase {
    private func infoReply(
        status: UInt8 = 0, count: UInt8, active: UInt8, paired: UInt16, marks: [UInt8] = []
    ) -> [UInt8] {
        var r = [UInt8](repeating: 0, count: 32)
        r[0] = UInt8(ascii: "A")
        r[1] = UInt8(ascii: "S")
        r[2] = 1
        r[3] = 0x81
        r[4] = status
        r[5] = count
        r[6] = active
        r[7] = UInt8(paired & 0xFF)
        r[8] = UInt8(paired >> 8)
        for (i, m) in marks.enumerated() { r[9 + i] = m }
        return r
    }

    func testRequestsCarryMagicVersionCommandAndArguments() {
        let r = ZmkHostProtocol.request(ZmkHostProtocol.setMark, [2, 0xA5])
        XCTAssertEqual(r.count, 32)
        XCTAssertEqual(Array(r.prefix(6)), [UInt8(ascii: "A"), UInt8(ascii: "S"), 1, 0x03, 2, 0xA5])
        XCTAssertTrue(r.dropFirst(6).allSatisfy { $0 == 0 })
    }

    func testInfoReplyParsesProfilesActivePairedAndMarks() throws {
        // Read from the user's Reviung41 on the Mac (2026-11-14): 5 profiles, on profile 1,
        // profiles 0 and 1 paired, Windows marked profile 0.
        let ch = try XCTUnwrap(
            ZmkHostProtocol.parseInfo(infoReply(count: 5, active: 1, paired: 0b00011, marks: [0xA5, 0, 0, 0, 0])))
        XCTAssertEqual(ch.count, 5)
        XCTAssertEqual(ch.current, 1)
        XCTAssertEqual(ch.paired, [true, true, false, false, false])
        XCTAssertEqual(ch.marks, [0xA5, 0, 0, 0, 0])
        XCTAssertEqual(ch.markedOtherChannel, 0)
        XCTAssertEqual(ch.automaticTarget, 0)
        XCTAssertEqual(ch.noun, "Profile")
        XCTAssertTrue(ch.isUnpaired(2))
        XCTAssertFalse(ch.isUnpaired(0))
    }

    func testAutomaticTargetFallsBackToTheOnlyOtherPairedProfile() throws {
        // Paired with two computers, the other hasn't marked its profile: profile 1 it is.
        var ch = try XCTUnwrap(
            ZmkHostProtocol.parseInfo(infoReply(count: 5, active: 0, paired: 0b00011, marks: [0xA5])))
        XCTAssertNil(ch.markedOtherChannel)
        XCTAssertEqual(ch.automaticTarget, 1)

        // Three paired profiles, none marked: ambiguous.
        ch = try XCTUnwrap(ZmkHostProtocol.parseInfo(infoReply(count: 5, active: 0, paired: 0b00111, marks: [0xA5])))
        XCTAssertNil(ch.automaticTarget)

        // Three paired, one marked: the mark decides.
        ch = try XCTUnwrap(
            ZmkHostProtocol.parseInfo(infoReply(count: 5, active: 0, paired: 0b00111, marks: [0xA5, 0, 0xA5])))
        XCTAssertEqual(ch.automaticTarget, 2)

        // Logitech (pairing not reported), nothing marked: unknown, as before.
        let logitech = HostChannels(count: 3, current: 0, marks: [0xA5, 0, 0])
        XCTAssertNil(logitech.automaticTarget)
        XCTAssertFalse(logitech.isUnpaired(1))
        XCTAssertEqual(logitech.noun, "Channel")
    }

    func testBadInfoRepliesAreRejected() {
        var r = infoReply(count: 5, active: 0, paired: 1)
        r[0] = UInt8(ascii: "X")
        XCTAssertNil(ZmkHostProtocol.parseInfo(r), "wrong magic")
        r = infoReply(count: 5, active: 0, paired: 1)
        r[2] = 2
        XCTAssertNil(ZmkHostProtocol.parseInfo(r), "wrong version")
        XCTAssertNil(
            ZmkHostProtocol.parseInfo(
                infoReply(status: ZmkHostProtocol.statusUnsupported, count: 5, active: 0, paired: 1)),
            "error status")
        XCTAssertNil(ZmkHostProtocol.parseInfo(infoReply(count: 5, active: 5, paired: 1)), "active out of range")
        XCTAssertNil(ZmkHostProtocol.parseInfo(infoReply(count: 0, active: 0, paired: 0)), "no profiles")
        XCTAssertNil(ZmkHostProtocol.parseInfo(Array(infoReply(count: 5, active: 0, paired: 1).prefix(10))), "short")
    }

    func testRepliesAreMatchedToTheirCommand() {
        let r = infoReply(count: 5, active: 0, paired: 1)
        XCTAssertTrue(ZmkHostProtocol.isReply(r, to: ZmkHostProtocol.getInfo))
        XCTAssertFalse(ZmkHostProtocol.isReply(r, to: ZmkHostProtocol.select))
        XCTAssertFalse(
            ZmkHostProtocol.isReply(ZmkHostProtocol.request(ZmkHostProtocol.getInfo), to: ZmkHostProtocol.getInfo))
    }

    func testProfileLabels() {
        XCTAssertEqual(HostChannels.label(1, names: nil, noun: "Profile"), "Profile 2")
        XCTAssertEqual(HostChannels.label(0, names: ["PC"], noun: "Profile"), "Profile 1 · PC")
    }

    // MARK: - Composite

    func testCompositeUsesTheFirstBackendThatAnswersAndKeepsIt() async throws {
        let logitech = FakeHostSwitcher()
        logitech.supportedAddress = "34:88:5D:49:88:AF"
        let zmk = FakeHostSwitcher()
        zmk.supportedAddress = "E7:99:A2:72:A6:76"
        zmk.count = 5
        zmk.marks = [0, 0, 0, 0, 0]
        let composite = CompositeHostSwitcher([logitech, zmk])

        let ch = try await composite.channels("e7-99-a2-72-a6-76")
        XCTAssertEqual(ch?.count, 5)
        try await composite.switchTo("E7:99:A2:72:A6:76", channel: 1)
        XCTAssertTrue(zmk.calls.contains("switch:E799A272A676:1"), "\(zmk.calls)")
        XCTAssertEqual(logitech.calls, [])
        let k380 = try await composite.channels("34-88-5d-49-88-af")
        XCTAssertEqual(k380?.count, 3)

        let none = try await composite.channels("00:11:22:33:44:55")
        XCTAssertNil(none)
        do {
            try await composite.switchTo("00:11:22:33:44:55", channel: 1)
            XCTFail("expected an error for a device no backend answers for")
        } catch {}
        let mark = try await composite.markCurrentChannel("00:11:22:33:44:55")
        XCTAssertNil(mark)
    }

    // MARK: - Engine (spec §3 Disconnect flow)

    private let addr = "e7-99-a2-72-a6-76"
    private let digits = "E799A272A676"

    private func releaseKeyboard(releaseToChannel: Int? = nil, configure: (FakeHostSwitcher) -> Void) async -> (
        FakeHostSwitcher, MockBluetoothController
    ) {
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigStore(directory: tmp)
        store.config.source = USBSource(name: "Hub", vendorID: 0x05E3, productIDs: [0x0626])
        store.config.devices = [
            BTDevice(
                name: "Reviung41", address: addr, managePairing: false, connectDelayMs: 0,
                releaseToChannel: releaseToChannel)
        ]
        let bt = MockBluetoothController()
        let switcher = FakeHostSwitcher()
        switcher.supportedAddress = addr
        switcher.count = 5
        switcher.current = 1  // this Mac is profile 2
        switcher.marks = [0, 0, 0, 0, 0]
        configure(switcher)
        let engine = SelectionEngine(store: store, usb: MockUSBMonitor(), bt: bt, hostSwitcher: switcher)
        engine.sleepHook = { _ in }
        engine.refreshMonitoring()
        for added in [true, false] {
            engine.handleUSB((vendorID: 0x05E3, productID: 0x0626, added: added))
            await engine.evaluateNow()
            for _ in 0..<30 { await Task.yield() }
        }
        return (switcher, bt)
    }

    func testAKeyboardPairedWithTwoComputersGoesToTheOtherOneWithoutAMark() async {
        let (switcher, _) = await releaseKeyboard { $0.paired = [true, true, false, false, false] }
        XCTAssertTrue(switcher.calls.contains("switch:\(digits):0"), "\(switcher.calls)")
    }

    func testNeverSwitchesToAnUnpairedProfile() async {
        let (switcher, bt) = await releaseKeyboard(releaseToChannel: 4) {
            $0.paired = [true, true, false, false, false]
        }
        XCTAssertFalse(switcher.calls.contains { $0.hasPrefix("switch:") }, "profile 4 is empty: \(switcher.calls)")
        XCTAssertTrue(bt.calls.contains("disconnect:\(addr)"), "falls back to the usual release: \(bt.calls)")
    }

    func testWithSeveralPairedProfilesTheMarkDecides() async {
        // Three paired, profile 3 marked by the other computer: the mark decides.
        let (switcher, _) = await releaseKeyboard {
            $0.paired = [true, true, true, false, false]
            $0.marks = [0, 0, 0xA5, 0, 0]
        }
        XCTAssertTrue(switcher.calls.contains("switch:\(digits):2"), "\(switcher.calls)")
    }
}
