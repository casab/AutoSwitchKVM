import XCTest

@testable import AutoSwitchKVM

/// Spec §5a type 5 (mirrors windows/tests ChannelSwitchTests): a device that switches channel on
/// request (Logitech HID++ Change Host) is sent to the other computer's channel on release
/// instead of being disconnected or unpaired.
@MainActor
final class ChannelSwitchTests: XCTestCase {
    private let addr = "aa-bb-cc-dd-ee-ff"
    private let digits = "AABBCCDDEEFF"

    @MainActor
    private struct Fixture {
        let engine: SelectionEngine
        let bt: MockBluetoothController
        let switcher: FakeHostSwitcher
        let store: ConfigStore
        var device: BTDevice { store.config.devices[0] }
    }

    private func make(
        managePairing: Bool = false, releaseToChannel: Int? = nil, supported: Bool = true
    ) -> Fixture {
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigStore(directory: tmp)
        store.config.source = USBSource(name: "Hub", vendorID: 0x05E3, productIDs: [0x0626])
        store.config.devices = [
            BTDevice(
                name: "K380", address: addr, managePairing: managePairing, connectDelayMs: 0,
                releaseToChannel: releaseToChannel)
        ]
        let bt = MockBluetoothController()
        let switcher = FakeHostSwitcher()
        if supported { switcher.supportedAddress = addr }
        switcher.current = 1  // this Mac is on channel 2
        let engine = SelectionEngine(store: store, usb: MockUSBMonitor(), bt: bt, hostSwitcher: switcher)
        engine.sleepHook = { _ in }
        engine.lateReleaseMinInterval = 0
        engine.refreshMonitoring()  // so the mock's connect events reach the engine
        return Fixture(engine: engine, bt: bt, switcher: switcher, store: store)
    }

    private func arrive(_ f: Fixture) async {
        f.engine.handleUSB((vendorID: 0x05E3, productID: 0x0626, added: true))
        await f.engine.evaluateNow()
        await settle()
    }

    private func leave(_ f: Fixture) async {
        f.engine.handleUSB((vendorID: 0x05E3, productID: 0x0626, added: false))
        await f.engine.evaluateNow()
        await settle()
    }

    /// Let the background mark/late-release tasks run.
    private func settle() async {
        for _ in 0..<30 { await Task.yield() }
    }

    func testReleaseSendsTheDeviceToTheConfiguredChannelWithoutDisconnecting() async {
        let f = make(releaseToChannel: 1)
        await arrive(f)
        let btCalls = f.bt.calls.count
        await leave(f)

        XCTAssertTrue(f.switcher.calls.contains("switch:\(digits):0"), "\(f.switcher.calls)")
        XCTAssertEqual(f.bt.calls.count, btCalls, "no disconnect or unpair: \(f.bt.calls)")
        XCTAssertEqual(f.engine.statuses[f.device.id], .disconnected)
    }

    func testConnectedWhileSelectedMarksThisComputersChannelOnce() async {
        let f = make()
        await arrive(f)
        XCTAssertEqual(f.switcher.calls.filter { $0.hasPrefix("mark:") }, ["mark:\(digits):1"])
        XCTAssertEqual(f.switcher.marks[1], HostChannels.autoSwitchMark)

        // A second connect in the same session doesn't mark again.
        await f.engine.connectDeviceNow(f.device)
        await settle()
        XCTAssertEqual(f.switcher.calls.filter { $0.hasPrefix("mark:") }.count, 1)
    }

    func testAutomaticTargetIsTheChannelTheOtherComputerMarked() async {
        let f = make()
        f.switcher.marks = [HostChannels.autoSwitchMark, 0, 0]  // Windows marked channel 1
        await arrive(f)
        await leave(f)
        XCTAssertTrue(f.switcher.calls.contains("switch:\(digits):0"), "\(f.switcher.calls)")
    }

    func testUnknownTargetFallsBackToTheUsualRelease() async {
        let f = make()  // nothing marked, no setting
        await arrive(f)
        await leave(f)
        XCTAssertFalse(f.switcher.calls.contains { $0.hasPrefix("switch:") })
        XCTAssertTrue(f.bt.calls.contains("disconnect:\(addr)"), "\(f.bt.calls)")
    }

    func testATargetEqualToThisMacsChannelFallsBack() async {
        let f = make(releaseToChannel: 2)  // channel 2 is this Mac's
        await arrive(f)
        await leave(f)
        XCTAssertFalse(f.switcher.calls.contains { $0.hasPrefix("switch:") })
        XCTAssertTrue(f.bt.calls.contains("disconnect:\(addr)"))
    }

    func testDeviceWithoutChannelSwitchingIsReleasedAsUsual() async {
        let f = make(releaseToChannel: 1, supported: false)
        await arrive(f)
        await leave(f)
        XCTAssertEqual(f.switcher.calls, [])
        XCTAssertTrue(f.bt.calls.contains("disconnect:\(addr)"))
    }

    func testManagedPairingDevicesAreNeverChannelSwitched() async {
        let f = make(managePairing: true, releaseToChannel: 1)
        await arrive(f)
        await leave(f)
        XCTAssertEqual(f.switcher.calls, [], "no mark, no switch")
        XCTAssertTrue(f.bt.calls.contains("unpair:\(addr)"))
    }

    /// The keyboard was asleep at the switch and woke up on this Mac's channel: send it on.
    func testADeviceThatConnectsWhileSwitchedAwayIsSentOn() async {
        let f = make(releaseToChannel: 1)
        await arrive(f)
        await leave(f)
        let switches = f.switcher.calls.filter { $0.hasPrefix("switch:") }.count

        f.switcher.current = 1  // it came back to this Mac's channel on its own
        f.bt.simulateConnection(addr)
        await settle()
        XCTAssertEqual(f.switcher.calls.filter { $0.hasPrefix("switch:") }.count, switches + 1, "\(f.switcher.calls)")
    }

    // MARK: - Arrival: wait, don't call (hardware 2026-11-13: paging a K380 kept it out ~20 s)

    func testAKnownChannelDeviceIsWaitedForAndNeverCalled() async {
        let f = make(releaseToChannel: 1)  // known: a channel is set for it
        var polls = 0
        f.engine.sleepHook = { [bt = f.bt, addr] _ in
            polls += 1
            if polls == 3 { bt.preset(paired: true, connected: true, addr) }  // the other host sent it here
        }
        await arrive(f)

        XCTAssertFalse(f.bt.calls.contains { $0.hasPrefix("connect:") || $0.hasPrefix("pair:") }, "\(f.bt.calls)")
        XCTAssertEqual(f.engine.statuses[f.device.id], .connected)
    }

    func testAChannelDeviceThatDoesntComeOverEndsDisconnectedNotFailed() async {
        let f = make(releaseToChannel: 1)
        f.engine.channelArrivalWaitMs = 1000
        await arrive(f)
        XCTAssertFalse(f.bt.calls.contains { $0.hasPrefix("connect:") }, "\(f.bt.calls)")
        XCTAssertEqual(f.engine.statuses[f.device.id], .disconnected)
    }

    /// Learned at runtime: after it answered the channel query on a release, the next arrival waits.
    func testAReleaseTeachesTheEngineToWaitNextTime() async {
        let f = make()
        f.switcher.marks = [HostChannels.autoSwitchMark, 0, 0]
        await arrive(f)  // unknown yet: connected the usual way
        XCTAssertTrue(f.bt.calls.contains("connect:\(addr)"))
        await leave(f)  // answers the query, gets switched
        XCTAssertTrue(f.engine.switchesChannels(f.device))

        let connects = f.bt.calls.filter { $0.hasPrefix("connect:") }.count
        f.engine.channelArrivalWaitMs = 1000
        await arrive(f)
        XCTAssertEqual(f.bt.calls.filter { $0.hasPrefix("connect:") }.count, connects, "no call on the way back")
    }

    // MARK: - Fast switch-back (hardware 2026-11-13)

    /// Trackpad (pairing) first, then the K380 (channel switching, known).
    private func makeDesk() -> Fixture {
        let f = make(releaseToChannel: 1)
        f.store.config.devices.insert(
            BTDevice(name: "Trackpad", address: "11-22-33-44-55-66", managePairing: true, connectDelayMs: 0), at: 0)
        f.bt.pairLeavesLinkUp = true
        f.engine.refreshMonitoring()
        return f
    }

    /// The KVM comes back while the Mac is still releasing: nothing more is sent away or unpaired.
    func testAReleaseStopsWhenTheKVMComesBack() async {
        let f = makeDesk()
        f.engine.channelArrivalWaitMs = 1000
        await arrive(f)
        f.bt.preset(paired: true, connected: true, addr)  // the K380 is here

        var bounced = false
        f.bt.beforeDisconnect = { [engine = f.engine] in
            guard !bounced else { return }
            bounced = true
            engine.handleUSB((vendorID: 0x05E3, productID: 0x0626, added: true))  // back before the debounce ran
        }
        let before = f.bt.calls.count
        f.engine.handleUSB((vendorID: 0x05E3, productID: 0x0626, added: false))
        await f.engine.evaluateNow()
        await settle()

        let calls = Array(f.bt.calls[before...])
        XCTAssertFalse(calls.contains("unpair:11-22-33-44-55-66"), "the trackpad keeps its bond: \(calls)")
        XCTAssertFalse(f.switcher.calls.contains { $0.hasPrefix("switch:") }, "the K380 stays: \(f.switcher.calls)")
    }

    /// A slow trackpad pair doesn't keep the keyboard waiting in line.
    func testAChannelDeviceIsWaitedForAlongsideTheOthers() async {
        let f = makeDesk()
        var pairGate = false
        f.bt.beforePair = {
            while !pairGate { try? await Task.sleep(nanoseconds: 1_000_000) }
        }
        var polls = 0
        f.engine.sleepHook = { [bt = f.bt, addr] _ in
            polls += 1
            if polls == 2 { bt.preset(paired: true, connected: true, addr) }  // the K380 comes over
            await Task.yield()
        }
        f.engine.handleUSB((vendorID: 0x05E3, productID: 0x0626, added: true))
        let run = Task { await f.engine.evaluateNow() }

        let deadline = Date().addingTimeInterval(3)
        while f.engine.statuses[f.store.config.devices[1].id] != .connected, Date() < deadline {
            try? await Task.sleep(nanoseconds: 5_000_000)
        }
        XCTAssertEqual(
            f.engine.statuses[f.store.config.devices[1].id], .connected, "connected while the trackpad still pairs")
        XCTAssertNotEqual(f.engine.statuses[f.store.config.devices[0].id], .connected)
        pairGate = true
        await run.value
    }
}
