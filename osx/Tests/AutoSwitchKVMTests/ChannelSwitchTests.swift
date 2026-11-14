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
        engine.thisComputerName = "Test Mac"
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
        f.bt.preset(paired: true, connected: true, addr)  // it's on this Mac
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
        f.switcher.reachable = false  // asleep: the release can't send it
        await leave(f)
        let switches = f.switcher.calls.filter { $0.hasPrefix("switch:") }.count
        XCTAssertEqual(switches, 0)

        f.switcher.reachable = true  // it wakes up, still on this Mac's channel
        f.bt.simulateConnection(addr)
        await settle()
        XCTAssertEqual(f.switcher.calls.filter { $0.hasPrefix("switch:") }.count, switches + 1, "\(f.switcher.calls)")
    }

    /// Hardware 2026-11-14 (Reviung41 over LE): ZMK keeps its link to this Mac after switching
    /// profile, so the Mac keeps seeing it connected while it doesn't answer. No second send.
    func testALingeringLinkAfterTheSwitchIsNotALateRelease() async {
        let f = make(releaseToChannel: 1)
        await arrive(f)
        await leave(f)
        let switches = f.switcher.calls.filter { $0.hasPrefix("switch:") }.count
        XCTAssertEqual(switches, 1)

        f.switcher.reachable = false  // active on the other computer: HID traffic goes there
        f.bt.simulateConnection(addr)
        for _ in 0..<200 { await Task.yield() }
        XCTAssertEqual(f.switcher.calls.filter { $0.hasPrefix("switch:") }.count, switches, "\(f.switcher.calls)")
    }

    /// Firmware that answers over an inactive link reports the other computer's channel: leave it.
    /// Without the guard, Automatic would pick this Mac's marked channel and pull it back.
    func testALateReleaseLeavesADeviceThatIsOnTheOtherComputersChannel() async {
        let f = make()
        f.switcher.marks = [HostChannels.autoSwitchMark, 0, 0]  // Windows marked channel 1
        await arrive(f)  // seen here on channel 2, which the Mac marks
        await leave(f)  // sent to channel 1: the fake now reports current 0
        XCTAssertEqual(f.switcher.calls.filter { $0.hasPrefix("switch:") }, ["switch:\(digits):0"])
        let switches = 1

        f.bt.simulateConnection(addr)
        for _ in 0..<200 { await Task.yield() }
        XCTAssertEqual(f.switcher.calls.filter { $0.hasPrefix("switch:") }.count, switches, "\(f.switcher.calls)")
    }

    // MARK: - Where the device is (hardware 2026-11-14: ZMK keeps its link to every computer)

    /// The user brings it back by hand while the KVM is on the other computer: leave it here.
    func testADeviceBroughtBackWhileTheKVMIsAwayIsLeftHere() async {
        let f = make(releaseToChannel: 1)
        f.bt.preset(paired: true, connected: true, addr)
        await arrive(f)
        await leave(f)
        XCTAssertEqual(f.switcher.calls.filter { $0.hasPrefix("switch:") }.count, 1)

        f.switcher.current = 1  // its channel key
        f.bt.simulateConnection(addr)
        for _ in 0..<200 { await Task.yield() }
        XCTAssertEqual(f.switcher.calls.filter { $0.hasPrefix("switch:") }.count, 1, "\(f.switcher.calls)")
    }

    /// Stored names that don't tell which channel is this Mac's are read once a session, not on
    /// every check (a K380 over Classic takes ~1.5 s to read them).
    func testNamesThatDontTellAreNotReadAgain() async {
        let f = make()
        f.switcher.names = ["PC", nil, nil]
        f.switcher.paired = [true, true, true]  // two unnamed paired channels: can't tell
        f.bt.preset(paired: true, connected: true, addr)
        f.engine.noteSwitchesChannels(f.device.id)
        _ = await f.engine.isOnOwnChannel(f.device)
        _ = await f.engine.isOnOwnChannel(f.device)
        XCTAssertEqual(f.switcher.nameReads, 1)
    }

    // MARK: - A seed with the KVM away (launch, unpause, profile switch)

    /// The app starts while the KVM is on the other computer, with the keyboard on this Mac's
    /// channel (switched by hand): it stays, and isn't sent on later either.
    func testASeedLeavesAChannelSwitcherWhereItIs() async {
        let f = makeNamed()
        f.bt.preset(paired: true, connected: true, addr)
        f.engine.seed()
        await f.engine.evaluateNow()
        f.bt.simulateConnection(addr)
        for _ in 0..<200 { await Task.yield() }

        XCTAssertFalse(f.switcher.calls.contains { $0.hasPrefix("switch:") }, "\(f.switcher.calls)")
        XCTAssertFalse(f.bt.calls.contains { $0.hasPrefix("disconnect:") }, "\(f.bt.calls)")
    }

    /// A device that doesn't switch channels is still released by the seed, as before.
    func testASeedStillReleasesADeviceThatDoesntSwitchChannels() async {
        let f = make(supported: false)
        f.bt.preset(paired: true, connected: true, addr)
        f.engine.seed()
        await f.engine.evaluateNow()
        XCTAssertTrue(f.bt.calls.contains("disconnect:\(addr)"), "\(f.bt.calls)")
    }

    private func makeNamed() -> Fixture {
        let f = make()
        f.switcher.names = ["PC", "Test Mac", nil]
        f.switcher.paired = [true, true, false]
        f.switcher.marks = [HostChannels.autoSwitchMark, HostChannels.autoSwitchMark, 0]
        return f
    }

    /// Already on the other computer's channel when the KVM leaves (switched by hand; its link to
    /// this Mac is still up): nothing to send, nothing to disconnect.
    func testAReleaseLeavesADeviceAlreadyOnTheOtherComputersChannel() async {
        let f = makeNamed()
        f.bt.preset(paired: true, connected: true, addr)
        await arrive(f)
        f.switcher.current = 0  // its channel key: on the PC now, link to the Mac still up
        let btCalls = f.bt.calls.count
        await leave(f)
        XCTAssertFalse(f.switcher.calls.contains { $0.hasPrefix("switch:") }, "\(f.switcher.calls)")
        XCTAssertEqual(f.bt.calls.count, btCalls, "no disconnect: \(f.bt.calls)")
    }

    /// The link is up the whole time, but the keyboard is still on the PC's channel: it counts as
    /// arrived only once it's on this Mac's, and only this Mac's channel is marked.
    func testArrivalWaitsUntilTheDeviceIsOnThisMacsChannel() async {
        let f = makeNamed()
        f.switcher.current = 0
        f.bt.preset(paired: true, connected: true, addr)
        var polls = 0
        var statusBeforeSwitch: DeviceStatus?
        f.engine.sleepHook = { [switcher = f.switcher, engine = f.engine, id = f.device.id] _ in
            polls += 1
            if polls == 3 {
                statusBeforeSwitch = engine.statuses[id]
                switcher.current = 1  // the PC's AutoSwitch sent it here
            }
        }
        await arrive(f)
        XCTAssertGreaterThanOrEqual(polls, 3)
        XCTAssertNotEqual(statusBeforeSwitch, .connected)
        XCTAssertEqual(f.engine.statuses[f.device.id], .connected)
        XCTAssertFalse(f.bt.calls.contains { $0.hasPrefix("connect:") }, "never called: \(f.bt.calls)")
        XCTAssertFalse(f.switcher.calls.contains("mark:\(digits):0"), "\(f.switcher.calls)")
    }

    /// Switched to the other computer by hand while the KVM is here: no auto-reconnect.
    func testAChannelSwitcherLeavingWhileTheKVMIsHereIsNotPulledBack() async {
        let f = make(releaseToChannel: 1)
        f.bt.preset(paired: true, connected: true, addr)
        await arrive(f)
        XCTAssertEqual(f.engine.statuses[f.device.id], .connected)
        let btCalls = f.bt.calls.count

        f.bt.simulateConnectionDrop(addr)
        for _ in 0..<200 { await Task.yield() }
        XCTAssertFalse(f.bt.calls.dropFirst(btCalls).contains { $0.hasPrefix("connect:") }, "\(f.bt.calls)")
        XCTAssertFalse(f.switcher.calls.contains { $0.hasPrefix("switch:") })
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
        f.switcher.reachable = false  // on the other computer: doesn't answer here
        await arrive(f)  // unknown yet: connected the usual way
        XCTAssertTrue(f.bt.calls.contains("connect:\(addr)"))
        f.switcher.reachable = true
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

        // The K380 goes first; the KVM comes back while its channel is being queried.
        var bounced = false
        f.switcher.onChannels = { [engine = f.engine] in
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

    // MARK: - Late release timing (hardware 2026-11-13: the query 7 ms after the connection was too early)

    /// It lands while the KVM is away, but its control channel needs a moment: keep asking.
    func testALateReleaseKeepsAskingUntilTheDeviceAnswers() async {
        let f = make(releaseToChannel: 1)
        f.engine.channelArrivalWaitMs = 1000
        await arrive(f)
        f.switcher.reachable = false  // asleep at the release: not sent
        await leave(f)
        let switches = f.switcher.calls.filter { $0.hasPrefix("switch:") }.count

        f.switcher.reachable = false  // just connected: not answering yet
        f.switcher.current = 1  // it landed on this Mac's channel
        var polls = 0
        f.engine.sleepHook = { [switcher = f.switcher] _ in
            polls += 1
            if polls == 2 { switcher.reachable = true }
        }
        f.bt.simulateConnection(addr)
        for _ in 0..<200 { await Task.yield() }
        XCTAssertEqual(
            f.switcher.calls.filter { $0.hasPrefix("switch:") }.count, switches + 1, "sent on: \(f.switcher.calls)")
    }

    /// It lands while an arrival wait still holds it busy: sent on once the wait lets go.
    func testALateReleaseWaitsForABusyDeviceInsteadOfGivingUp() async {
        let f = make(releaseToChannel: 1)
        f.switcher.reachable = false  // still travelling when the release runs
        var pollsInWait = 0
        f.engine.sleepHook = { [engine = f.engine, bt = f.bt, switcher = f.switcher, addr] _ in
            pollsInWait += 1
            guard pollsInWait == 1 else { return }
            // While the arrival wait holds it: the KVM leaves, then the keyboard lands here.
            engine.handleUSB((vendorID: 0x05E3, productID: 0x0626, added: false))
            await engine.evaluateNow()
            switcher.reachable = true
            bt.simulateConnection(addr)
        }
        LogThreshold.shared.level = .debug
        defer { LogThreshold.shared.level = .info }
        await arrive(f)
        for _ in 0..<300 { await Task.yield() }
        XCTAssertTrue(
            f.switcher.calls.contains("switch:\(digits):0"),
            "sent on once free: \(f.switcher.calls) LOG: "
                + DebugLog.shared.entries.suffix(14).map(\.message).joined(separator: " || "))
    }

    /// The trackpad is listed first, but the K380's channel switch goes before its unpair.
    func testChannelSwitchesAreReleasedBeforeUnpairs() async {
        let f = makeDesk()
        f.engine.channelArrivalWaitMs = 1000
        await arrive(f)
        f.bt.preset(paired: true, connected: true, addr)
        f.switcher.current = 1
        var order: [String] = []
        f.bt.beforeDisconnect = { order.append("trackpad") }
        let switchesBefore = f.switcher.calls.count
        f.engine.handleUSB((vendorID: 0x05E3, productID: 0x0626, added: false))
        await f.engine.evaluateNow()
        XCTAssertGreaterThan(f.switcher.calls.count, switchesBefore)
        XCTAssertEqual(order, ["trackpad"])
        // The switch was recorded before the trackpad's disconnect started.
        XCTAssertTrue(f.switcher.calls.last?.hasPrefix("switch:") ?? false)
    }
}
