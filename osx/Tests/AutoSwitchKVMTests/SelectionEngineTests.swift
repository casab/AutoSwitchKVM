import XCTest

@testable import AutoSwitchKVM

@MainActor
final class SelectionEngineTests: XCTestCase {

    private let vendor: UInt16 = 0x05E3
    private let product: UInt16 = 0x0626
    private let addr = "3c-50-02-bf-22-45"

    private func makeStore() -> ConfigStore {
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        return ConfigStore(directory: tmp)
    }

    private func makeEngine(
        managePairing: Bool = false,
        enabled: Bool = true
    ) -> (SelectionEngine, MockBluetoothController, ConfigStore) {
        let store = makeStore()
        store.config.source = USBSource(name: "Hub", vendorID: vendor, productIDs: [product])
        store.config.devices = [
            BTDevice(
                name: "Trackpad", address: addr,
                enabled: enabled, managePairing: managePairing)
        ]
        let usb = MockUSBMonitor()
        let bt = MockBluetoothController()
        let engine = SelectionEngine(store: store, usb: usb, bt: bt)
        return (engine, bt, store)
    }

    func testConnectsWhenSourceArrives() async {
        let (engine, bt, _) = makeEngine()
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertTrue(engine.selected)
        XCTAssertTrue(bt.connectedAddrs.contains(addr))
    }

    func testDisconnectsWhenSourceLeaves() async {
        let (engine, bt, _) = makeEngine()
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()
        XCTAssertTrue(engine.selected)

        engine.handleUSB((vendorID: vendor, productID: product, added: false))
        await engine.evaluateNow()

        XCTAssertFalse(engine.selected)
        XCTAssertFalse(bt.connectedAddrs.contains(addr))
    }

    func testPairsBeforeConnectWhenManagePairing() async {
        let (engine, bt, _) = makeEngine(managePairing: true)
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        guard let pairIndex = bt.calls.firstIndex(of: "pair:\(addr)"),
            let connectIndex = bt.calls.firstIndex(of: "connect:\(addr)")
        else {
            return XCTFail("expected both pair and connect calls; got \(bt.calls)")
        }
        XCTAssertLessThan(pairIndex, connectIndex, "pair must happen before connect")
    }

    func testUnpairsOnLeaveWhenManagePairing() async {
        let (engine, bt, _) = makeEngine(managePairing: true)
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        engine.handleUSB((vendorID: vendor, productID: product, added: false))
        await engine.evaluateNow()

        XCTAssertTrue(bt.calls.contains("unpair:\(addr)"))
    }

    func testDoesNotUnpairOnLeaveWhenNotManagingPairing() async {
        let (engine, bt, _) = makeEngine(managePairing: false)
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()
        engine.handleUSB((vendorID: vendor, productID: product, added: false))
        await engine.evaluateNow()

        XCTAssertFalse(bt.calls.contains("unpair:\(addr)"))
    }

    func testIgnoresUnrelatedUSBDevice() async {
        let (engine, bt, _) = makeEngine()
        engine.handleUSB((vendorID: 0x1234, productID: 0x5678, added: true))
        await engine.evaluateNow()

        XCTAssertFalse(engine.selected)
        XCTAssertTrue(bt.connectedAddrs.isEmpty)
    }

    func testDisabledDeviceIsNotConnected() async {
        let (engine, bt, _) = makeEngine(enabled: false)
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertTrue(engine.selected)  // source is present
        XCTAssertTrue(bt.connectedAddrs.isEmpty)  // but disabled device is left alone
    }

    func testMultiDeviceSourceStaysSelectedUntilAllGone() async {
        let store = makeStore()
        let p1: UInt16 = 0x0626
        let p2: UInt16 = 0x0610
        store.config.source = USBSource(name: "KVM", vendorID: vendor, productIDs: [p1, p2])
        store.config.devices = [BTDevice(name: "Trackpad", address: addr, enabled: true)]
        let usb = MockUSBMonitor()
        let bt = MockBluetoothController()
        let engine = SelectionEngine(store: store, usb: usb, bt: bt)

        engine.handleUSB((vendorID: vendor, productID: p1, added: true))
        engine.handleUSB((vendorID: vendor, productID: p2, added: true))
        await engine.evaluateNow()
        XCTAssertTrue(engine.selected)

        // One member leaves — still selected because the other remains.
        engine.handleUSB((vendorID: vendor, productID: p1, added: false))
        await engine.evaluateNow()
        XCTAssertTrue(engine.selected)

        // Last member leaves — now deselected.
        engine.handleUSB((vendorID: vendor, productID: p2, added: false))
        await engine.evaluateNow()
        XCTAssertFalse(engine.selected)
    }

    func testPausedSkipsAutomaticConnect() async {
        let (engine, bt, store) = makeEngine()
        store.config.paused = true

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertTrue(engine.selected, "selection should reflect real presence even while paused")
        XCTAssertTrue(bt.connectedAddrs.isEmpty, "paused automation should not connect")
    }

    func testResumingFromPauseReconcilesState() async {
        let (engine, bt, store) = makeEngine()
        store.config.paused = true

        // Source present while paused: presence reflected, but nothing connected.
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()
        XCTAssertTrue(bt.connectedAddrs.isEmpty)

        // Resume → should connect to match the present source.
        store.config.paused = false
        await engine.evaluateNow()
        XCTAssertTrue(bt.connectedAddrs.contains(addr))
    }

    func testConnectAllNowWorksRegardlessOfSelection() async {
        let (engine, bt, _) = makeEngine()
        // Source is NOT present, so automation wouldn't connect.
        await engine.connectAllNowImpl()
        XCTAssertTrue(bt.connectedAddrs.contains(addr))
    }

    func testDisconnectAllNowDisconnects() async {
        let (engine, bt, _) = makeEngine()
        try? await bt.connect(addr)
        await engine.disconnectAllNowImpl()
        XCTAssertFalse(bt.connectedAddrs.contains(addr))
    }

    func testBluetoothOffSkipsConnect() async {
        let (engine, bt, _) = makeEngine()
        bt.powered = false
        await engine.refreshPower()
        XCTAssertFalse(engine.bluetoothPowered)

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertTrue(engine.selected)  // presence still tracked
        XCTAssertTrue(bt.connectedAddrs.isEmpty)  // but no connect while BT is off
        XCTAssertEqual(engine.statuses.values.first, .bluetoothOff)
    }

    func testBackoffIsExponentialAndCapped() {
        XCTAssertEqual(SelectionEngine.backoffSeconds(base: 2, attempt: 1), 2)
        XCTAssertEqual(SelectionEngine.backoffSeconds(base: 2, attempt: 2), 4)
        XCTAssertEqual(SelectionEngine.backoffSeconds(base: 2, attempt: 3), 8)
        XCTAssertEqual(SelectionEngine.backoffSeconds(base: 2, attempt: 4), 16)
        XCTAssertEqual(SelectionEngine.backoffSeconds(base: 2, attempt: 6), 30)  // capped
    }

    /// The spec caps the retry backoff at 30 s — jitter must not push the real sleep past the cap.
    func testJitteredBackoffNeverExceedsCap() {
        for attempt in 1...8 {
            for _ in 0..<50 {
                XCTAssertLessThanOrEqual(
                    SelectionEngine.backoffNanos(base: 2, attempt: attempt),
                    UInt64(30_000_000_000))
            }
        }
    }

    func testConnectsInDeviceListOrder() async {
        let store = makeStore()
        store.config.source = USBSource(name: "Hub", vendorID: vendor, productIDs: [product])
        store.config.devices = [
            BTDevice(name: "First", address: "aa-aa", enabled: true),
            BTDevice(name: "Second", address: "bb-bb", enabled: true),
        ]
        let usb = MockUSBMonitor()
        let bt = MockBluetoothController()
        let engine = SelectionEngine(store: store, usb: usb, bt: bt)

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertEqual(bt.calls, ["connect:aa-aa", "connect:bb-bb"])
    }

    func testAlreadyConnectedSkipsRedundantConnect() async {
        let (engine, bt, _) = makeEngine()
        try? await bt.connect(addr)  // pretend it's already connected
        let callsBefore = bt.calls.count

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertEqual(bt.calls.count, callsBefore, "should not re-connect an already-connected device")
    }

    // MARK: - Spec conformance (mirrored in windows/tests SelectionEngineTests)

    /// Spec §3: on giving up after connectRetryMax attempts, the device status is `error`.
    func testRetryExhaustionSetsErrorStatus() async {
        let (engine, bt, store) = makeEngine()
        store.config.connectRetryMax = 1  // keep the test free of real backoff sleeps
        bt.connectError = BTError.connectFailed(-1)

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        guard case .error(let message)? = engine.statuses.values.first else {
            return XCTFail("expected .error, got \(String(describing: engine.statuses.values.first))")
        }
        XCTAssertTrue(message.contains("gave up"), "exhaustion message expected, got: \(message)")
    }

    /// A bond record can survive while the device no longer honors its link key (the other host
    /// re-paired it, or it was power-cycled) — seen on hardware as endless "pair: skipped" +
    /// openConnection timeouts until the user manually removes the device in System Settings.
    /// Retries must escalate to a fresh re-pair instead of trusting isPaired().
    func testStaleBondEscalatesToRepairOnRetry() async {
        let (engine, bt, store) = makeEngine(managePairing: true)
        store.config.connectRetryMax = 2
        store.config.connectRetrySecs = 0  // no real backoff sleep in the test

        try? await bt.pair(addr)  // the bond record exists...
        bt.connectError = BTError.connectFailed(-1)  // ...but the device won't honor it
        bt.afterUnpair = { bt.connectError = nil }  // a fresh bond makes connect work again

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        guard let unpairIndex = bt.calls.firstIndex(of: "unpair:\(addr)") else {
            return XCTFail("retry must unpair the stale bond; calls: \(bt.calls)")
        }
        guard let lastPairIndex = bt.calls.lastIndex(of: "pair:\(addr)") else {
            return XCTFail("expected a pair call; calls: \(bt.calls)")
        }
        XCTAssertLessThan(unpairIndex, lastPairIndex, "unpair must precede the fresh pair")
        XCTAssertTrue(bt.connectedAddrs.contains(addr))
        XCTAssertEqual(engine.statuses.values.first, .connected)
    }

    /// The escalation is for managed bonds only — unmanaged devices must never be unpaired.
    func testUnmanagedRetryNeverUnpairs() async {
        let (engine, bt, store) = makeEngine(managePairing: false)
        store.config.connectRetryMax = 2
        store.config.connectRetrySecs = 0
        bt.connectError = BTError.connectFailed(-1)

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertFalse(bt.calls.contains("unpair:\(addr)"))
    }

    /// Spec §3: a passive drop while the source is present, BT is on, and no transition is in
    /// flight is an unexpected disconnect.
    func testUnexpectedDisconnectFiresWhenDeviceDropsWhileSelected() async {
        let (engine, bt, store) = makeEngine()
        store.config.autoReconnectOnDrop = false  // isolate the notification/status behavior
        var dropped: BTDevice?
        engine.onUnexpectedDisconnect = { dropped = $0 }
        engine.refreshMonitoring()

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()
        XCTAssertEqual(engine.statuses.values.first, .connected)

        bt.fireConnectionChange(addr, false)  // passive drop, source still present

        XCTAssertEqual(dropped?.name, "Trackpad")
        XCTAssertEqual(engine.statuses.values.first, .disconnected)
    }

    // MARK: - Auto-reconnect on unexpected drop

    /// A device that drops unexpectedly while the source is present must reconnect by itself —
    /// on hardware, a mid-session drop otherwise leaves the device dead until manual action.
    func testAutoReconnectsAfterUnexpectedDrop() async {
        let (engine, bt, _) = makeEngine()
        engine.autoReconnectDelayMs = 0
        engine.refreshMonitoring()
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()
        XCTAssertTrue(bt.connectedAddrs.contains(addr))

        bt.simulateConnectionDrop(addr)

        await waitUntil({ bt.connectedAddrs.contains(addr) }, "device was not auto-reconnected")
        XCTAssertEqual(engine.statuses.values.first, .connected)
    }

    /// One reconnect per drop, rate-limited — a flapping device must not cause a reconnect storm.
    func testAutoReconnectIsRateLimited() async {
        let (engine, bt, _) = makeEngine()
        engine.autoReconnectDelayMs = 0
        engine.refreshMonitoring()
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        bt.simulateConnectionDrop(addr)
        await waitUntil({ bt.connectedAddrs.contains(addr) }, "first drop must auto-reconnect")

        let callsAfterFirst = bt.calls.count
        bt.simulateConnectionDrop(addr)  // second drop inside the rate window
        for _ in 0..<20 {  // give a (wrong) reconnect a chance to run
            await Task.yield()
            try? await Task.sleep(nanoseconds: 5_000_000)
        }

        XCTAssertEqual(bt.calls.count, callsAfterFirst, "second drop within the rate window must not reconnect")
        XCTAssertEqual(engine.statuses.values.first, .disconnected)
    }

    /// Paused means paused: no automatic reconnect either.
    func testAutoReconnectSkippedWhilePaused() async {
        let (engine, bt, store) = makeEngine()
        engine.autoReconnectDelayMs = 0
        engine.refreshMonitoring()
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        store.config.paused = true
        bt.simulateConnectionDrop(addr)
        for _ in 0..<20 {
            await Task.yield()
            try? await Task.sleep(nanoseconds: 5_000_000)
        }

        XCTAssertFalse(bt.connectedAddrs.contains(addr))
    }

    /// The trigger requires the source to be present (manual connects while switched away
    /// must not fight the other host over a dropped device).
    func testAutoReconnectSkippedWhenSourceAbsent() async {
        let (engine, bt, _) = makeEngine()
        engine.autoReconnectDelayMs = 0
        engine.refreshMonitoring()
        await engine.connectAllNowImpl()  // manual connect; source absent
        XCTAssertTrue(bt.connectedAddrs.contains(addr))

        bt.simulateConnectionDrop(addr)
        for _ in 0..<20 {
            await Task.yield()
            try? await Task.sleep(nanoseconds: 5_000_000)
        }

        XCTAssertFalse(bt.connectedAddrs.contains(addr))
    }

    /// Spec §3 connect flow: pair → (no HID on the pairing link) drop the transient post-pair
    /// link → clean reconnect, in exactly that order.
    func testManagedConnectDropsTransientLinkThenReconnects() async {
        let (engine, bt, _) = makeEngine(managePairing: true)
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertEqual(bt.calls, ["pair:\(addr)", "disconnect:\(addr)", "connect:\(addr)"])
    }

    /// Hardware 2026-11-12: dropping the pairing link while bluetoothd was bringing HID up on it
    /// wedged the trackpad (HID refused with STATUS 706 until power-cycled). When HID comes up on
    /// the pairing link, that link is kept: no disconnect, no reconnect.
    func testHIDOnThePairingLinkIsKept() async {
        let (engine, bt, _) = makeEngine(managePairing: true)
        bt.pairLeavesLinkUp = true
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertEqual(bt.calls, ["pair:\(addr)"])
        XCTAssertTrue(bt.connectedAddrs.contains(addr))
    }

    /// A pairing link that stays up without HID is still dropped and replaced by a clean connect.
    func testPairingLinkWithoutHIDIsReplaced() async {
        let (engine, bt, _) = makeEngine(managePairing: true)
        bt.pairLeavesLinkUp = true
        bt.hidWorks = false
        bt.beforeConnect = { bt.hidWorks = true }
        engine.sleepHook = { _ in }  // skip the bounded HID wait on the pairing link
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertEqual(bt.calls, ["pair:\(addr)", "disconnect:\(addr)", "connect:\(addr)"])
        XCTAssertTrue(bt.connectedAddrs.contains(addr))
    }

    /// Spec §3: the per-device connect delay re-checks cancellation after the wait.
    func testConnectDelayAbortsWhenSupersededDuringWait() async {
        let store = makeStore()
        store.config.source = USBSource(name: "Hub", vendorID: vendor, productIDs: [product])
        store.config.devices = [
            BTDevice(name: "Trackpad", address: addr, enabled: true, connectDelayMs: 500)
        ]
        let usb = MockUSBMonitor()
        let bt = MockBluetoothController()
        let engine = SelectionEngine(store: store, usb: usb, bt: bt)

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        let arrival = Task { @MainActor in await engine.evaluateNow() }

        // Supersede during the connect delay: the source leaves and the departure run starts.
        try? await Task.sleep(nanoseconds: 50_000_000)
        engine.handleUSB((vendorID: vendor, productID: product, added: false))
        await engine.evaluateNow()
        await arrival.value

        XCTAssertFalse(bt.calls.contains("connect:\(addr)"), "the delayed connect must abort once superseded")
    }

    /// Spec §3: on Bluetooth off → on, re-seed and reconnect if the source is present.
    func testPowerOnReseedsAndReconnects() async {
        let store = makeStore()
        store.config.source = USBSource(name: "Hub", vendorID: vendor, productIDs: [product])
        store.config.devices = [BTDevice(name: "Trackpad", address: addr, enabled: true)]
        let usb = MockUSBMonitor()
        let bt = MockBluetoothController()
        let engine = SelectionEngine(store: store, usb: usb, bt: bt)

        bt.powered = false
        await engine.refreshPower()
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()
        XCTAssertTrue(bt.connectedAddrs.isEmpty)

        usb.attachedDevices = [USBDeviceInfo(vendorID: vendor, productID: product, name: "Hub")]
        bt.powered = true
        await engine.refreshPower()  // → seed(): presence re-read from the snapshot
        await engine.evaluateNow()  // drive the (normally debounced) evaluation deterministically

        XCTAssertTrue(bt.connectedAddrs.contains(addr))
    }

    // MARK: - Stale-run cancellation

    /// Spin the main actor until `condition` holds (bounded), letting suspended tasks progress.
    private func waitUntil(
        _ condition: @MainActor () -> Bool, timeout: TimeInterval = 3.0,
        _ message: String = "condition not met in time"
    ) async {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            if Date() > deadline { return XCTFail(message) }
            await Task.yield()
            try? await Task.sleep(nanoseconds: 5_000_000)
        }
    }

    /// A KVM flipped away and back while the departure's disconnect is still in flight must not
    /// let the stale disconnect run continue: it would unpair/disconnect the devices the new
    /// arrival run just brought up, leaving them unusable with nothing scheduled to retry.
    func testStaleDisconnectRunAbortsWhenSourceReturns() async {
        let addr2 = "aa-bb-cc-dd-ee-ff"
        let store = makeStore()
        store.config.source = USBSource(name: "Hub", vendorID: vendor, productIDs: [product])
        store.config.devices = [
            BTDevice(name: "Trackpad", address: addr, enabled: true, managePairing: true),
            BTDevice(name: "Keyboard", address: addr2, enabled: true, managePairing: true),
        ]
        let usb = MockUSBMonitor()
        let bt = MockBluetoothController()
        let engine = SelectionEngine(store: store, usb: usb, bt: bt)

        // Source arrives; both devices come up paired + connected.
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()
        XCTAssertTrue(bt.connectedAddrs.contains(addr) && bt.connectedAddrs.contains(addr2))

        // Arm a one-shot gate: the next disconnect (the departure run's first) suspends until released.
        var release: CheckedContinuation<Void, Never>?
        var armed = true
        bt.beforeDisconnect = {
            guard armed else { return }
            armed = false
            await withCheckedContinuation { release = $0 }
        }

        // Source leaves; the departure run suspends inside device 1's disconnect.
        engine.handleUSB((vendorID: vendor, productID: product, added: false))
        let departure = Task { @MainActor in await engine.evaluateNow() }
        await waitUntil({ release != nil }, "departure run never reached the gated disconnect")

        // Source returns; the arrival run reconnects everything while the stale run is suspended.
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()
        XCTAssertTrue(bt.connectedAddrs.contains(addr) && bt.connectedAddrs.contains(addr2))

        // Release the stale run: it must notice it is stale and stop — no unpair of device 1,
        // and device 2 untouched.
        release?.resume()
        await departure.value

        XCTAssertTrue(bt.pairedAddrs.contains(addr), "stale run unpaired device 1 after the new run paired it")
        XCTAssertTrue(bt.pairedAddrs.contains(addr2), "stale run unpaired device 2 after the new run paired it")
        XCTAssertTrue(
            bt.connectedAddrs.contains(addr2),
            "stale run disconnected device 2 after the new run connected it")
        XCTAssertFalse(bt.calls.contains("unpair:\(addr2)"), "stale run must abort before device 2")
    }

    /// Manual "Disconnect all now" must invalidate an in-flight automation connect run so the
    /// automation does not keep connecting devices the user just asked to release.
    func testDisconnectAllNowAbortsInFlightConnectRun() async {
        let addr2 = "aa-bb-cc-dd-ee-ff"
        let store = makeStore()
        store.config.source = USBSource(name: "Hub", vendorID: vendor, productIDs: [product])
        store.config.devices = [
            BTDevice(name: "First", address: addr, enabled: true),
            BTDevice(name: "Second", address: addr2, enabled: true),
        ]
        let usb = MockUSBMonitor()
        let bt = MockBluetoothController()
        let engine = SelectionEngine(store: store, usb: usb, bt: bt)

        // One-shot gate: the arrival run suspends inside device 1's connect.
        var release: CheckedContinuation<Void, Never>?
        var armed = true
        bt.beforeConnect = {
            guard armed else { return }
            armed = false
            await withCheckedContinuation { release = $0 }
        }

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        let arrival = Task { @MainActor in await engine.evaluateNow() }
        await waitUntil({ release != nil }, "arrival run never reached the gated connect")

        // User hits "Disconnect all now" while the connect run is mid-flight.
        await engine.disconnectAllNowImpl()

        release?.resume()
        await arrival.value

        XCTAssertFalse(
            bt.connectedAddrs.contains(addr2),
            "the superseded connect run must not continue on to device 2")
    }

    /// The bond is what blocks the other host (Magic Trackpad): even if the disconnect call
    /// fails, a managePairing device must still be unpaired on departure.
    func testUnpairStillAttemptedWhenDisconnectThrows() async {
        let (engine, bt, _) = makeEngine(managePairing: true)
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()
        XCTAssertTrue(bt.pairedAddrs.contains(addr))

        bt.disconnectError = BTError.disconnectFailed(-1)
        engine.handleUSB((vendorID: vendor, productID: product, added: false))
        await engine.evaluateNow()

        XCTAssertTrue(bt.calls.contains("unpair:\(addr)"), "unpair must run even when disconnect throws")
        XCTAssertFalse(bt.pairedAddrs.contains(addr), "the bond must be released for the other host")
        XCTAssertEqual(engine.statuses.values.first, .disconnected)
    }
}

/// `withTimeout` must honor its deadline even when the wrapped operation cannot observe
/// cancellation (the synchronous IOBluetooth calls) — and not report the timeout only after
/// the blocked call eventually returns.
@MainActor
final class WithTimeoutTests: XCTestCase {
    func testTimesOutAtDeadlineDespiteUncancellableOperation() async {
        let start = Date()
        do {
            _ = try await withTimeout(1) { () -> Int in
                // Simulates a blocking IOBluetooth call: ignores cancellation for ~5 s.
                let deadline = Date().addingTimeInterval(5)
                while Date() < deadline {
                    try? await Task.sleep(nanoseconds: 50_000_000)
                }
                return 42
            }
            XCTFail("expected BTError.timeout")
        } catch {
            XCTAssertTrue(error is BTError, "expected BTError.timeout, got \(error)")
        }
        XCTAssertLessThan(
            Date().timeIntervalSince(start), 3.0,
            "the timeout must surface at ~1 s, not after the abandoned operation finishes")
    }

    func testReturnsOperationValueWhenFasterThanDeadline() async throws {
        let value = try await withTimeout(5) { 7 }
        XCTAssertEqual(value, 7)
    }
}
