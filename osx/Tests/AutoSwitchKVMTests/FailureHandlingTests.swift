import XCTest

@testable import AutoSwitchKVM

/// Mirrors windows/tests/AutoSwitchKVM.Core.Tests/FailureHandlingTests.cs: failure classification
/// (SPECIFICATION.md §3), release retry/reporting, manual actions routed through the engine, and
/// system sleep/wake. Delays are instant (`sleepHook` no-op) so every retry path runs fast.
@MainActor
final class FailureHandlingTests: XCTestCase {

    private let vendor: UInt16 = 0x05E3
    private let product: UInt16 = 0x0626
    private let addr = "3c-50-02-bf-22-45"

    // Representative raw codes, as IOBluetooth reports them.
    private let busy = BTError.connectFailed(0x0D)  // HCI connection rejected (limited resources)
    private let inProgress = BTError.connectFailed(Int32(bitPattern: 0xE000_02D5))  // kIOReturnBusy
    private let authFailure = BTError.connectFailed(0x05)  // HCI authentication failure
    private let noDevice = BTError.connectFailed(Int32(bitPattern: 0xE000_02C0))  // kIOReturnNoDevice
    private let ioTimeout = BTError.connectFailed(Int32(bitPattern: 0xE000_02D6))  // kIOReturnTimeout

    private var notices: [(title: String, body: String)] = []

    @MainActor
    private struct Fixture {
        let engine: SelectionEngine
        let bt: MockBluetoothController
        let usb: MockUSBMonitor
        let store: ConfigStore
        var device: BTDevice { store.config.devices[0] }
        var status: DeviceStatus? { engine.statuses[device.id] }
    }

    override func setUp() async throws {
        notices = []
    }

    private func make(managePairing: Bool = true, retries: Int = 3) -> Fixture {
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigStore(directory: tmp)
        store.config.source = USBSource(name: "Hub", vendorID: vendor, productIDs: [product])
        store.config.devices = [
            BTDevice(name: "Trackpad", address: addr, enabled: true, managePairing: managePairing)
        ]
        store.config.connectRetryMax = retries
        store.config.connectRetrySecs = 0
        let usb = MockUSBMonitor()
        let bt = MockBluetoothController()
        let engine = SelectionEngine(store: store, usb: usb, bt: bt)
        engine.sleepHook = { _ in }  // instant connect delay / settle / backoff / soft retries
        engine.autoReconnectDelayMs = 0
        engine.onNotice = { [weak self] title, body in self?.notices.append((title, body)) }
        return Fixture(engine: engine, bt: bt, usb: usb, store: store)
    }

    private func sourceArrives(_ f: Fixture) {
        f.engine.handleUSB((vendorID: vendor, productID: product, added: true))
    }

    private func sourceLeaves(_ f: Fixture) {
        f.engine.handleUSB((vendorID: vendor, productID: product, added: false))
    }

    private func count(_ f: Fixture, _ prefix: String) -> Int {
        f.bt.calls.filter { $0.hasPrefix(prefix) }.count
    }

    /// Let queued main-actor work (late events, scheduled reconnects) run.
    private func settle() async {
        for _ in 0..<30 { await Task.yield() }
    }

    private func spinUntil(_ condition: @MainActor () -> Bool, _ message: String) async {
        let deadline = Date().addingTimeInterval(3)
        while !condition() {
            if Date() > deadline { return XCTFail(message) }
            await Task.yield()
            try? await Task.sleep(nanoseconds: 2_000_000)
        }
    }

    // MARK: - Classification

    func testBusyFailuresRetryWithoutConsumingAttempts() async {
        let f = make(managePairing: false, retries: 1)
        f.bt.connectErrors = [busy, busy, busy]

        sourceArrives(f)
        await f.engine.evaluateNow()

        XCTAssertEqual(count(f, "connect:"), 4, "three soft retries plus the successful try")
        XCTAssertEqual(f.status, .connected, "refusals must not spend the single hard attempt")
    }

    func testBusyBudgetExhaustionFallsBackToHardAttemptsThenGivesUp() async {
        let f = make(managePairing: false, retries: 2)
        f.engine.maxSoftRetries = 2
        f.bt.connectError = busy

        sourceArrives(f)
        await f.engine.evaluateNow()

        XCTAssertEqual(count(f, "connect:"), 4, "2 soft retries, then 2 hard attempts")
        guard case .error(let message)? = f.status else {
            return XCTFail("expected .error, got \(String(describing: f.status))")
        }
        XCTAssertTrue(message.contains("gave up"), message)
    }

    func testOperationInProgressIsASoftRetry() async {
        let f = make(managePairing: false, retries: 1)
        f.bt.connectErrors = [inProgress]

        sourceArrives(f)
        await f.engine.evaluateNow()

        XCTAssertEqual(f.status, .connected)
        XCTAssertEqual(count(f, "connect:"), 2)
    }

    func testAuthenticationFailureRepairsFromScratch() async {
        let f = make(managePairing: true)
        f.bt.connectErrors = [authFailure]

        sourceArrives(f)
        await f.engine.evaluateNow()

        guard let unpair = f.bt.calls.firstIndex(of: "unpair:\(addr)"),
            let lastPair = f.bt.calls.lastIndex(of: "pair:\(addr)")
        else { return XCTFail("expected a re-pair from scratch; calls: \(f.bt.calls)") }
        XCTAssertLessThan(unpair, lastPair)
        XCTAssertEqual(f.status, .connected)
    }

    func testTimeoutRepairsFromScratch() async {
        let f = make(managePairing: true)
        f.bt.connectErrors = [ioTimeout]

        sourceArrives(f)
        await f.engine.evaluateNow()

        XCTAssertEqual(count(f, "unpair:"), 1, "a timeout may mean a stale bond (seen on hardware)")
        XCTAssertEqual(f.status, .connected)
    }

    /// A refusal means the other host still holds the device; destroying our fresh bond in
    /// response only forces another full pairing ceremony.
    func testRefusalDoesNotDestroyTheBond() async {
        let f = make(managePairing: true)
        f.bt.connectErrors = [busy]

        sourceArrives(f)
        await f.engine.evaluateNow()

        XCTAssertEqual(count(f, "unpair:"), 0, "calls: \(f.bt.calls)")
        XCTAssertEqual(f.status, .connected)
    }

    func testDeviceNotFoundRetriesWithoutUnpairing() async {
        let f = make(managePairing: true)
        f.bt.connectErrors = [noDevice]

        sourceArrives(f)
        await f.engine.evaluateNow()

        XCTAssertEqual(count(f, "unpair:"), 0, "calls: \(f.bt.calls)")
        XCTAssertEqual(count(f, "connect:"), 2)
        XCTAssertEqual(f.status, .connected)
    }

    func testInvalidAddressGivesUpImmediately() async {
        let f = make(managePairing: false, retries: 5)
        f.bt.connectError = BTError.invalidAddress("nonsense")

        sourceArrives(f)
        await f.engine.evaluateNow()

        XCTAssertEqual(count(f, "connect:"), 1, "retrying a malformed address can never succeed")
        guard case .error(let message)? = f.status else { return XCTFail("expected .error") }
        XCTAssertTrue(message.contains("gave up"), message)
    }

    func testGiveUpNoticeAndStatusCarryTheCause() async {
        let f = make(managePairing: false, retries: 1)
        f.bt.connectError = ioTimeout

        sourceArrives(f)
        await f.engine.evaluateNow()

        guard case .error(let message)? = f.status else { return XCTFail("expected .error") }
        XCTAssertTrue(message.contains("kIOReturnTimeout"), "status should name the cause: \(message)")
        let notice = notices.first { $0.title == "Connection failed" }
        XCTAssertNotNil(notice)
        XCTAssertTrue(notice?.body.contains("kIOReturnTimeout") == true, "notice: \(String(describing: notice))")
    }

    // MARK: - HID verification and churn (first joint run, 2026-11-12)

    /// Hardware: openConnection returned OK in 1 ms on a bare link, the app said "connected ✓", and
    /// the link idled out 15 s later — HID never came up. Re-pairing didn't help either; the loop
    /// only churned a wedged trackpad. A link without HID is a failure, and repeated ones end the
    /// run early with a hint instead of exhausting every retry.
    func testLinkWithoutHIDIsNotSuccessAndEndsTheRunEarly() async {
        let f = make(managePairing: true, retries: 6)
        f.bt.hidWorks = false

        sourceArrives(f)
        await f.engine.evaluateNow()

        guard case .error(let message)? = f.status else {
            return XCTFail("expected .error, got \(String(describing: f.status))")
        }
        XCTAssertTrue(message.lowercased().contains("power-cycle"), message)
        XCTAssertEqual(count(f, "connect:"), f.engine.maxHidFailures, "should stop early, not after 6 tries")
        XCTAssertEqual(count(f, "unpair:"), 0, "a link without HID is not a stale bond: no re-pair churn")
        XCTAssertTrue(notices.contains { $0.title == "Connection failed" })
    }

    func testBackendWithoutAnHIDCheckTrustsTheLink() async {
        let f = make(managePairing: false)
        f.bt.hidUnknown = true

        sourceArrives(f)
        await f.engine.evaluateNow()

        XCTAssertEqual(f.status, .connected)
    }

    /// Hardware: `-remove` returns at once but the system finishes the unpair up to ~2.6 s later;
    /// a pair started right away failed with HCI 0x02 every time.
    func testStaleBondRepairWaitsForTheUnpairToSettle() async {
        let f = make(managePairing: true)
        f.bt.connectErrors = [ioTimeout]
        var sleepsAfter: [(ms: Int, lastCall: String?)] = []
        f.engine.sleepHook = { ms in sleepsAfter.append((ms, f.bt.calls.last)) }

        sourceArrives(f)
        await f.engine.evaluateNow()

        XCTAssertTrue(
            sleepsAfter.contains { $0.ms == f.engine.postUnpairSettleMs && $0.lastCall == "unpair:\(addr)" },
            "expected a settle between the stale-bond unpair and the re-pair: \(sleepsAfter)")
        XCTAssertEqual(f.status, .connected)
    }

    /// The Windows agent found this in its port: after one hard failure, every *soft* retry
    /// re-ran the stale-bond unpair. It must run once per hard failure.
    func testSoftRetriesAfterAHardFailureUnpairOnlyOnce() async {
        let f = make(managePairing: true)
        f.bt.connectErrors = [ioTimeout, busy, busy]

        sourceArrives(f)
        await f.engine.evaluateNow()

        XCTAssertEqual(count(f, "unpair:"), 1, "calls: \(f.bt.calls)")
        XCTAssertEqual(f.status, .connected)
    }

    // MARK: - Races at the edges

    /// The source leaves while the pairing ceremony is still running: the release runs first, then
    /// the ceremony completes anyway. The stale run must not go on to connect, and the bond that
    /// landed after the release must be removed again — otherwise this Mac holds the device while
    /// switched away and the other host can't pair.
    func testPairThatLandsAfterAReleaseIsUndone() async {
        let f = make(managePairing: true)
        let gate = Gate()
        f.bt.beforePair = { await gate.hook() }

        sourceArrives(f)
        let arrival = Task { @MainActor in await f.engine.evaluateNow() }
        await spinUntil({ gate.reached }, "the arrival run never reached the pairing step")

        sourceLeaves(f)
        await f.engine.evaluateNow()  // the release runs while the ceremony is in flight
        let callsAtRelease = f.bt.calls.count

        gate.open()  // worst case: the ceremony completes anyway
        await arrival.value

        let after = Array(f.bt.calls[callsAtRelease...])
        XCTAssertFalse(after.contains("connect:\(addr)"), "the stale run connected after the release: \(after)")
        XCTAssertFalse(f.bt.pairedAddrs.contains(addr), "the late bond must be removed again: \(after)")
        XCTAssertFalse(f.bt.connectedAddrs.contains(addr))
        XCTAssertEqual(f.status, .disconnected)
    }

    // MARK: - Release

    func testReleaseRetriesAFailedUnpairOnce() async {
        let f = make(managePairing: true)
        sourceArrives(f)
        await f.engine.evaluateNow()
        f.bt.unpairErrors = [BTError.unpairFailed("still paired after -remove")]

        sourceLeaves(f)
        await f.engine.evaluateNow()

        XCTAssertEqual(count(f, "unpair:"), 2)
        XCTAssertFalse(f.bt.pairedAddrs.contains(addr))
        XCTAssertEqual(f.status, .disconnected)
        XCTAssertFalse(notices.contains { $0.title == "Release failed" })
    }

    func testReleaseFailureIsReported() async {
        let f = make(managePairing: true)
        sourceArrives(f)
        await f.engine.evaluateNow()
        f.bt.unpairError = BTError.unpairFailed("still paired after -remove")

        sourceLeaves(f)
        await f.engine.evaluateNow()

        guard case .error(let message)? = f.status else {
            return XCTFail("expected .error, got \(String(describing: f.status))")
        }
        XCTAssertTrue(message.contains("still paired"), message)
        XCTAssertTrue(notices.contains { $0.title == "Release failed" }, "a failed release must not be silent")
    }

    // MARK: - Manual actions go through the engine

    /// Settings ▸ Unpair while selected: the stack's late "disconnected" event used to look like an
    /// unexpected drop, and auto-reconnect re-paired the device the user had just released.
    func testManualUnpairWhileSelectedDoesNotTriggerAutoReconnect() async {
        let f = make(managePairing: true)
        f.engine.refreshMonitoring()
        sourceArrives(f)
        await f.engine.evaluateNow()
        XCTAssertEqual(f.status, .connected)
        let before = f.bt.calls.count

        await f.engine.unpairDeviceNow(f.device)
        f.bt.fireConnectionChange(addr, false)  // the late event from the stack
        await settle()

        XCTAssertEqual(Array(f.bt.calls[before...]), ["unpair:\(addr)"], "no auto-reconnect after a manual unpair")
        XCTAssertEqual(f.status, .disconnected)
    }

    func testManualDisconnectWhileSelectedDoesNotTriggerAutoReconnect() async {
        let f = make(managePairing: false)
        f.engine.refreshMonitoring()
        sourceArrives(f)
        await f.engine.evaluateNow()
        let before = f.bt.calls.count

        await f.engine.disconnectDeviceNow(f.device)
        f.bt.fireConnectionChange(addr, false)
        await settle()

        XCTAssertEqual(Array(f.bt.calls[before...]), ["disconnect:\(addr)"])
        XCTAssertEqual(f.status, .disconnected)
    }

    func testManualConnectSupersedesInFlightAutomation() async {
        let f = make(managePairing: false)
        let gate = Gate()
        f.bt.beforeConnect = { await gate.hook() }

        sourceArrives(f)
        let arrival = Task { @MainActor in await f.engine.evaluateNow() }
        await spinUntil({ gate.reached }, "the arrival run never reached openConnection")

        await f.engine.connectDeviceNow(f.device)
        XCTAssertEqual(f.status, .connected)

        gate.open()
        await arrival.value
        XCTAssertEqual(f.status, .connected, "the superseded run must not overwrite the manual result")
        XCTAssertTrue(f.bt.connectedAddrs.contains(addr))
    }

    func testManualPairAndUnpairReportFailures() async {
        let f = make(managePairing: true)
        f.bt.pairErrors = [BTError.pairFailed("completion", code: 0x05)]

        let pairError = await f.engine.pairDeviceNow(f.device)
        XCTAssertNotNil(pairError)
        XCTAssertTrue(pairError?.contains("authentication failure") == true, String(describing: pairError))
        guard case .error? = f.status else { return XCTFail("a failed manual pair must show an error status") }

        let unpairError = await f.engine.unpairDeviceNow(f.device)
        XCTAssertNil(unpairError)
        XCTAssertEqual(f.status, .disconnected)
    }

    // MARK: - System sleep / wake

    /// The sleep release used to unpair through the controller directly: the resulting disconnect
    /// looked like an unexpected drop and auto-reconnect re-paired the device during sleep.
    func testSleepReleasesThroughTheEngineAndBlocksAutoReconnect() async {
        let f = make(managePairing: true)
        f.engine.refreshMonitoring()
        sourceArrives(f)
        await f.engine.evaluateNow()

        await f.engine.prepareForSleep()
        XCTAssertFalse(f.bt.pairedAddrs.contains(addr), "the bond must be released before sleep")
        XCTAssertEqual(f.status, .disconnected)
        let before = f.bt.calls.count

        f.bt.fireConnectionChange(addr, false)  // late event while going to sleep
        await settle()

        XCTAssertEqual(f.bt.calls.count, before, "nothing may re-pair while the Mac goes to sleep: \(f.bt.calls)")
    }

    func testSuspendedEngineIgnoresUSBUntilWakeThenReconnects() async {
        let f = make(managePairing: false)
        await f.engine.prepareForSleep()

        sourceArrives(f)
        await f.engine.evaluateNow()
        XCTAssertTrue(f.engine.selected, "presence is still tracked while suspended")
        XCTAssertEqual(count(f, "connect:"), 0, "no automation while suspended")

        f.usb.attachedDevices = [USBDeviceInfo(vendorID: vendor, productID: product, name: "Hub")]
        f.engine.resumeAfterWake()
        await f.engine.evaluateNow()  // drive the (normally debounced) evaluation deterministically

        XCTAssertTrue(f.bt.connectedAddrs.contains(addr))
        XCTAssertEqual(f.status, .connected)
    }

    /// A connect still running when sleep begins must not leave the device connected.
    func testConnectThatCompletesAfterSleepBeganIsReleasedAgain() async {
        let f = make(managePairing: false)
        let gate = Gate()
        f.bt.beforeConnect = { await gate.hook() }

        sourceArrives(f)
        let arrival = Task { @MainActor in await f.engine.evaluateNow() }
        await spinUntil({ gate.reached }, "the arrival run never reached openConnection")

        await f.engine.prepareForSleep()
        gate.open()
        await arrival.value

        XCTAssertFalse(f.bt.connectedAddrs.contains(addr), "calls: \(f.bt.calls)")
    }

    // MARK: - Code decoding

    func testIOBluetoothCodesAreNamedAndClassified() {
        XCTAssertEqual(BTCode.kind(0x04), .timeout)  // page timeout
        XCTAssertEqual(BTCode.kind(0x05), .authenticationFailed)
        XCTAssertEqual(BTCode.kind(0x06), .authenticationFailed)  // PIN or key missing
        XCTAssertEqual(BTCode.kind(0x0D), .deviceBusy)
        XCTAssertEqual(BTCode.kind(0x18), .deviceBusy)  // pairing not allowed
        XCTAssertEqual(BTCode.kind(Int32(bitPattern: 0xE000_02D5)), .operationInProgress)
        XCTAssertEqual(BTCode.kind(Int32(bitPattern: 0xE000_02D6)), .timeout)
        XCTAssertEqual(BTCode.kind(Int32(bitPattern: 0xE000_02C0)), .deviceNotFound)
        XCTAssertEqual(BTCode.kind(-1), .unknown)

        XCTAssertTrue(BTCode.describe(Int32(bitPattern: 0xE000_02D6)).hasPrefix("0xE00002D6 kIOReturnTimeout"))
        XCTAssertTrue(BTCode.describe(5).hasPrefix("HCI 0x05 authentication failure"))
        XCTAssertTrue(BTCode.describe(-1).contains("unrecognized"))

        XCTAssertEqual(BTError.pairFailed("completion", code: 0x06).kind, .authenticationFailed)
        XCTAssertEqual(BTError.pairFailed("start").kind, .unknown)
        XCTAssertEqual(BTError.timeout(seconds: 15).kind, .timeout)
        XCTAssertTrue(BTFailureKind.deviceBusy.isSoft)
        XCTAssertFalse(BTFailureKind.deviceBusy.mayBeStaleBond)
        XCTAssertTrue(BTFailureKind.unknown.mayBeStaleBond)
    }
    // MARK: - Pair after a recent unpair (KVM bounce, hardware 2026-11-12)

    /// The source comes back right after a release: the new pair waits until the system has had
    /// `postUnpairSettleMs` to finish removing the old bond (it deletes the key ~1.5 s after the
    /// link drops), instead of pairing into the unpair.
    func testPairRightAfterAReleaseWaitsForTheUnpairToSettle() async {
        let f = make()
        sourceArrives(f)
        await f.engine.evaluateNow()
        sourceLeaves(f)
        await f.engine.evaluateNow()
        XCTAssertEqual(f.bt.calls.last, "unpair:\(addr)")

        var sleeps: [Int] = []
        let before = f.bt.calls.count
        f.engine.sleepHook = { ms in sleeps.append(ms) }
        sourceArrives(f)
        await f.engine.evaluateNow()

        XCTAssertEqual(f.bt.calls[before...].first, "pair:\(addr)")
        XCTAssertTrue(f.bt.connectedAddrs.contains(addr))
        let settle = sleeps.first ?? 0
        XCTAssertGreaterThan(settle, f.engine.postUnpairSettleMs - 500, "waited out the unpair before pairing")
        XCTAssertLessThanOrEqual(settle, f.engine.postUnpairSettleMs)
    }

    /// A release superseded mid-unpair keeps running; the newer connect run must not start its
    /// pair until that unpair has returned.
    func testPairWaitsForAnUnpairStillInFlight() async {
        let f = make()
        sourceArrives(f)
        await f.engine.evaluateNow()

        var unpairing = false
        var releaseUnpair = false
        f.bt.beforeUnpair = {
            unpairing = true
            while !releaseUnpair { try? await Task.sleep(nanoseconds: 1_000_000) }
        }
        sourceLeaves(f)
        let release = Task { await f.engine.evaluateNow() }
        await spinUntil({ unpairing }, "the release reached its unpair")

        sourceArrives(f)
        let connect = Task { await f.engine.evaluateNow() }
        try? await Task.sleep(nanoseconds: 150_000_000)
        XCTAssertEqual(count(f, "pair:"), 1, "no new pair while the unpair is still running")

        releaseUnpair = true
        await release.value
        await connect.value
        let lastUnpair = f.bt.calls.lastIndex(of: "unpair:\(addr)") ?? -1
        let lastPair = f.bt.calls.lastIndex(of: "pair:\(addr)") ?? -1
        XCTAssertGreaterThan(lastPair, lastUnpair, "the new pair ran after the unpair returned")
        XCTAssertTrue(f.bt.connectedAddrs.contains(addr))
    }
}

/// The persistent log file (`~/Library/Logs/AutoSwitchKVM/app-yyyyMMdd.log` in the app).
final class FileLogTests: XCTestCase {
    private func tempDir() -> URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("filelog-\(UUID().uuidString)")
    }

    func testWritesLevelCategoryAndMessageToADailyFile() throws {
        let dir = tempDir()
        let log = FileLog()
        log.start(directory: dir)

        log.write(level: "WARN", category: "engine", message: "Trackpad: unpair failed", date: Date())
        log.write(level: "INFO", category: "usb", message: "source gone", date: Date())
        log.flush()

        let url = try XCTUnwrap(log.fileURL)
        XCTAssertTrue(url.lastPathComponent.hasPrefix("app-") && url.pathExtension == "log", url.path)
        let text = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(text.contains("WARN   [engine]  Trackpad: unpair failed"), text)
        XCTAssertTrue(text.contains("INFO   [usb]  source gone"), text)
    }

    func testDisabledUntilStarted() {
        let log = FileLog()
        log.write(level: "INFO", category: "app", message: "dropped", date: Date())
        log.flush()
        XCTAssertNil(log.fileURL)
    }

    func testPrunesOldLogFilesOnStart() throws {
        let dir = tempDir()
        let fm = FileManager.default
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        let old = dir.appendingPathComponent("app-20200101.log")
        let recent = dir.appendingPathComponent("app-20991231.log")
        let unrelated = dir.appendingPathComponent("notes.txt")
        for url in [old, recent, unrelated] { XCTAssertTrue(fm.createFile(atPath: url.path, contents: Data("x".utf8))) }
        let monthAgo = Date(timeIntervalSinceNow: -30 * 86_400)
        try fm.setAttributes([.modificationDate: monthAgo], ofItemAtPath: old.path)
        try fm.setAttributes([.modificationDate: monthAgo], ofItemAtPath: unrelated.path)

        FileLog().start(directory: dir, retentionDays: 14)

        XCTAssertFalse(fm.fileExists(atPath: old.path), "old app-*.log must be pruned")
        XCTAssertTrue(fm.fileExists(atPath: recent.path))
        XCTAssertTrue(fm.fileExists(atPath: unrelated.path), "only app-*.log files are ours to delete")
    }
}
