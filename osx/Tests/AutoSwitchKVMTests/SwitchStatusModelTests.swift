import XCTest

@testable import AutoSwitchKVM

/// The switch status panel's logic (spec: osx/docs/superpowers/specs/2026-11-16-switch-status-panel-design.md):
/// which rows it shows, what they say, and when it hides. Mirrors Windows' StatusPopup.
@MainActor
final class SwitchStatusModelTests: XCTestCase {
    private let trackpad = BTDevice(name: "Magic Trackpad", address: "11-22-33-44-55-66", managePairing: true)
    private let k380 = BTDevice(name: "Keyboard K380", address: "aa-bb-cc-dd-ee-ff", managePairing: false)
    private let speaker = BTDevice(name: "Soundcore", address: "f4-2b-7d-12-c8-21", managePairing: false)
    private let t0 = Date(timeIntervalSince1970: 0)
    private var all: [BTDevice] { [trackpad, k380, speaker] }

    private func model() -> SwitchStatusModel {
        let m = SwitchStatusModel()
        m.showSwitches = true
        m.showDrops = false
        return m
    }

    private func update(
        _ m: SwitchStatusModel, _ statuses: [UUID: DeviceStatus], selected: Bool, evaluated: Bool = true,
        devices: [BTDevice]? = nil, asleep: Set<UUID> = [], sent: Set<UUID> = [], switchers: Set<UUID> = [],
        at seconds: Double = 0
    ) {
        m.update(
            devices: devices ?? all, statuses: statuses, selected: selected, evaluated: evaluated, asleep: asleep,
            sentAway: sent, channelSwitchers: switchers, now: t0.addingTimeInterval(seconds))
    }

    private func all(_ status: DeviceStatus) -> [UUID: DeviceStatus] {
        Dictionary(uniqueKeysWithValues: all.map { ($0.id, status) })
    }

    private func row(_ m: SwitchStatusModel, _ d: BTDevice) -> SwitchStatusRow? {
        m.rows.first { $0.id == d.id }
    }

    func testLaunchShowsNothing() {
        let m = model()
        update(m, all(.connected), selected: true)
        XCTAssertFalse(m.visible)
        XCTAssertTrue(m.rows.isEmpty)
        update(m, all(.connected), selected: true)
        XCTAssertFalse(m.visible)
        XCTAssertTrue(m.rows.isEmpty)
    }

    func testASwitchShowsEachChangingDeviceLive() {
        let m = model()
        update(m, all(.disconnected), selected: false)
        var s = all(.disconnected)
        s[trackpad.id] = .connecting
        update(m, s, selected: true)
        XCTAssertTrue(m.visible)
        XCTAssertEqual(m.header, "KVM → this Mac")
        XCTAssertEqual(m.rows.count, 1)
        XCTAssertEqual(row(m, trackpad)?.text, "Connecting…")
        XCTAssertEqual(row(m, trackpad)?.tone, .busy)

        s[trackpad.id] = .connected
        update(m, s, selected: true, at: 1)
        XCTAssertEqual(row(m, trackpad)?.text, "Connected")
        XCTAssertEqual(row(m, trackpad)?.tone, .ok)
    }

    func testReleaseTexts() {
        let m = model()
        update(m, all(.connected), selected: true)
        update(m, all(.connecting), selected: false, sent: [k380.id])
        update(m, all(.disconnected), selected: false, sent: [k380.id], at: 1)
        XCTAssertEqual(m.header, "KVM → the other computer")
        XCTAssertEqual(row(m, trackpad)?.text, "Released")
        XCTAssertEqual(row(m, k380)?.text, "Sent to the other computer")
        XCTAssertEqual(row(m, speaker)?.text, "Disconnected")
        XCTAssertEqual(row(m, speaker)?.tone, .ok)
    }

    func testAsleepIsCalm() {
        let m = model()
        update(m, all(.disconnected), selected: false)
        update(m, [k380.id: .connecting], selected: true)
        update(m, [k380.id: .disconnected], selected: true, asleep: [k380.id], at: 1)
        XCTAssertEqual(row(m, k380)?.text, "Asleep: wake it to connect")
        XCTAssertEqual(row(m, k380)?.tone, .calm)
    }

    func testHidesAfterHoldTimes() {
        let m = model()
        update(m, all(.disconnected), selected: false)
        update(m, [trackpad.id: .connected, k380.id: .disconnected, speaker.id: .disconnected], selected: true)
        // From disconnected straight to connected is a change within a switch (not a launch).
        XCTAssertTrue(m.visible)
        m.tick(now: t0.addingTimeInterval(3.4), hovering: false)
        XCTAssertTrue(m.visible)
        m.tick(now: t0.addingTimeInterval(3.5), hovering: false)
        XCTAssertFalse(m.visible)
        XCTAssertTrue(m.rows.isEmpty)

        // A problem holds it 8 s (the next switch).
        update(
            m, [trackpad.id: .connecting, k380.id: .disconnected, speaker.id: .disconnected], selected: false, at: 10)
        update(
            m, [trackpad.id: .error("gave up"), k380.id: .disconnected, speaker.id: .disconnected], selected: false,
            at: 10)
        m.tick(now: t0.addingTimeInterval(17.9), hovering: false)
        XCTAssertTrue(m.visible)
        XCTAssertEqual(row(m, trackpad)?.tone, .problem)
        m.tick(now: t0.addingTimeInterval(18.0), hovering: false)
        XCTAssertFalse(m.visible)

        // Never while something is connecting (the next switch; the trackpad is quiet until it connects).
        update(
            m, [trackpad.id: .error("gave up"), k380.id: .connecting, speaker.id: .disconnected], selected: true, at: 20
        )
        m.tick(now: t0.addingTimeInterval(80), hovering: false)
        XCTAssertTrue(m.visible, "never while something is connecting")
    }

    func testHoverKeepsItOpen() {
        let m = model()
        update(m, all(.disconnected), selected: false)
        update(m, [trackpad.id: .connected], selected: true)
        m.tick(now: t0.addingTimeInterval(10), hovering: true)
        XCTAssertTrue(m.visible)
        m.tick(now: t0.addingTimeInterval(10), hovering: false)
        XCTAssertFalse(m.visible)
    }

    func testANewBurstClearsOldRows() {
        let m = model()
        update(m, all(.disconnected), selected: false)
        update(m, all(.connecting), selected: true)
        XCTAssertEqual(m.rows.count, 3)
        update(m, [trackpad.id: .disconnected, k380.id: .connecting, speaker.id: .connecting], selected: false, at: 1)
        XCTAssertEqual(m.header, "KVM → the other computer")
        XCTAssertEqual(m.rows.map(\.id), [trackpad.id])
    }

    func testRemovedDeviceLosesItsRow() {
        let m = model()
        update(m, all(.disconnected), selected: false)
        update(m, [speaker.id: .connecting], selected: true)
        XCTAssertNotNil(row(m, speaker))
        update(m, [speaker.id: .connecting], selected: true, devices: [trackpad, k380], at: 1)
        XCTAssertNil(row(m, speaker))
    }

    func testOffShowsNothingButDropsWhenAsked() {
        let m = model()
        m.showSwitches = false
        update(m, all(.disconnected), selected: false)
        update(m, all(.connecting), selected: true)
        XCTAssertFalse(m.visible)
        m.noteDrop(trackpad, now: t0)
        XCTAssertFalse(m.visible)

        m.showDrops = true
        m.noteDrop(trackpad, now: t0)
        XCTAssertTrue(m.visible)
        XCTAssertEqual(m.rows.count, 1)
        XCTAssertEqual(row(m, trackpad)?.text, "Dropped unexpectedly")
        XCTAssertEqual(row(m, trackpad)?.tone, .problem)
    }

    // MARK: - Final review findings (2026-11-16)

    /// The real launch: the engine publishes nothing and "not here" before its first evaluation,
    /// then "here" and every device Connecting while the seed connects. None of that is a KVM move.
    func testTheLaunchSequenceShowsNothing() {
        let m = model()
        update(m, [:], selected: false, evaluated: false)
        update(m, all(.connecting), selected: true)
        update(m, all(.connected), selected: true, at: 1)
        XCTAssertFalse(m.visible)
        XCTAssertTrue(m.rows.isEmpty)
        // The first real move after that does show.
        update(m, all(.connecting), selected: false, at: 5)
        XCTAssertTrue(m.visible)
        XCTAssertEqual(m.rows.count, 3)
    }

    /// The engine sets the device Disconnected, then reports the drop: the drop row must stay.
    func testADropRowKeepsItsTextWhenTheDisconnectArrives() {
        let m = model()
        m.showDrops = true
        update(m, all(.connected), selected: true)
        m.noteDrop(trackpad, now: t0)
        update(m, [trackpad.id: .disconnected, k380.id: .connected, speaker.id: .connected], selected: true)
        XCTAssertEqual(row(m, trackpad)?.text, "Dropped unexpectedly")
        XCTAssertEqual(row(m, trackpad)?.tone, .problem)
        // It reconnects: the row follows again.
        update(m, [trackpad.id: .connected, k380.id: .connected, speaker.id: .connected], selected: true, at: 1)
        XCTAssertEqual(row(m, trackpad)?.text, "Connected")
    }

    /// "Sent to the other computer" only for a device the engine actually sent; a channel switcher
    /// that didn't come over while the KVM is here says so.
    func testSentOnlyWhenActuallySentAndDidntComeOver() {
        let m = model()
        update(m, all(.connected), selected: true)
        update(m, all(.connecting), selected: false)
        update(m, all(.disconnected), selected: false, at: 1)
        XCTAssertEqual(row(m, k380)?.text, "Disconnected", "not sent: the release fell back to a disconnect")

        update(m, all(.connecting), selected: true, at: 2)
        update(
            m, [trackpad.id: .connected, k380.id: .disconnected, speaker.id: .connected], selected: true,
            switchers: [k380.id], at: 3)
        XCTAssertEqual(row(m, k380)?.text, "Didn't come over: press its channel key")
        XCTAssertEqual(row(m, k380)?.tone, .calm)
    }

    /// A connect cut short goes to Idle, then the release sets Disconnected: still a row.
    func testIdleThenDisconnectedInABurstShows() {
        let m = model()
        update(m, all(.disconnected), selected: false)
        update(m, [speaker.id: .connecting], selected: true)
        update(m, [speaker.id: .idle], selected: true, at: 1)
        update(m, [speaker.id: .disconnected], selected: false, at: 2)
        XCTAssertEqual(row(m, speaker)?.text, "Disconnected")
    }

    /// A device that ended up not connected (or with a problem) while the KVM is here is reported
    /// once, then left out (its retries, later switches) until it connects again (Windows'
    /// StatusChangeFilter, 2026-11-16: a switched-off speaker kept popping the panel).
    func testASwitchedOffDeviceIsReportedOnceUntilItConnects() {
        let m = model()
        update(m, all(.disconnected), selected: false)
        update(m, [speaker.id: .connecting], selected: true)
        update(m, [speaker.id: .error("didn't answer")], selected: true, at: 1)
        XCTAssertEqual(row(m, speaker)?.tone, .problem)
        m.dismiss()

        update(m, [speaker.id: .disconnected], selected: true, at: 20)
        XCTAssertTrue(m.rows.isEmpty, "its retries don't pop the panel")
        update(m, [speaker.id: .disconnected], selected: false, at: 30)
        update(m, [speaker.id: .connecting], selected: true, at: 40)
        update(m, [speaker.id: .error("didn't answer")], selected: true, at: 41)
        XCTAssertNil(row(m, speaker), "nor the next switches")

        update(m, [speaker.id: .connected], selected: true, at: 50)
        XCTAssertEqual(row(m, speaker)?.text, "Connected", "until it connects again")
    }

    /// A release for the other computer isn't "ended up not connected".
    func testAReleaseDoesntSilenceADevice() {
        let m = model()
        update(m, all(.connected), selected: true)
        update(m, [speaker.id: .connecting], selected: false)
        update(m, [speaker.id: .disconnected], selected: false, at: 1)
        update(m, [speaker.id: .connecting], selected: true, at: 10)
        XCTAssertEqual(row(m, speaker)?.text, "Connecting…")
    }

    // MARK: - Aligned with Windows (2026-11-17, Windows' reply on the switch-status note)

    /// The panel hiding ends the switch: a wake's reconnects later (no KVM move) show nothing.
    func testAWakeAfterASwitchShowsNothing() {
        let m = model()
        update(m, all(.disconnected), selected: false)
        update(m, all(.connected), selected: true)
        m.tick(now: t0.addingTimeInterval(4), hovering: false)
        XCTAssertFalse(m.visible)
        update(m, all(.connecting), selected: true, at: 100)  // wake: the engine reconnects
        update(m, all(.connected), selected: true, at: 101)
        XCTAssertFalse(m.visible)
        XCTAssertTrue(m.rows.isEmpty)
    }

    /// A quiet device (it didn't connect) that connects later, e.g. woken up, shows even outside a
    /// switch: "it's back".
    func testAQuietDeviceThatConnectsLaterShows() {
        let m = model()
        update(m, all(.disconnected), selected: false)
        update(m, [speaker.id: .connecting], selected: true)
        update(m, [speaker.id: .disconnected], selected: true, at: 1)
        m.tick(now: t0.addingTimeInterval(10), hovering: false)
        XCTAssertFalse(m.visible)
        update(m, [speaker.id: .connected], selected: true, at: 200)
        XCTAssertTrue(m.visible)
        XCTAssertEqual(row(m, speaker)?.text, "Connected")
    }

    /// "Didn't come over: press its channel key" only for a channel switcher; a speaker has no
    /// channel key.
    func testNotConnectedForADeviceWithoutChannels() {
        let m = model()
        update(m, all(.disconnected), selected: false)
        update(m, [speaker.id: .connecting], selected: true)
        update(m, [speaker.id: .disconnected], selected: true, at: 1)
        XCTAssertEqual(row(m, speaker)?.text, "Not connected")
    }
}

/// The panel's show/hide decision (final review 2026-11-16: it relied on `isVisible`, which stays
/// true during a fade-out, so rows arriving mid-fade left it ordered in at alpha 0: invisible but
/// catching clicks, and the next switch showed nothing).
final class PanelPresenceTests: XCTestCase {
    func testShowsAgainWhenRowsReturnDuringTheFadeOut() {
        var p = PanelPresence()
        XCTAssertEqual(p.step(wanted: true), .show)
        XCTAssertEqual(p.step(wanted: true), .none)
        XCTAssertEqual(p.step(wanted: false), .hide)
        XCTAssertEqual(p.step(wanted: true), .show, "rows came back before the fade-out finished")
        XCTAssertFalse(p.orderOutAfterFade, "shown again: keep it ordered in")
        XCTAssertEqual(p.step(wanted: false), .hide)
        XCTAssertTrue(p.orderOutAfterFade)
        XCTAssertEqual(p.step(wanted: false), .none)
    }
}
