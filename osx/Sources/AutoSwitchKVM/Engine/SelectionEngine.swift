import Combine
import Foundation

enum DeviceStatus: Equatable {
    case idle
    case connecting
    case connected
    case disconnected
    case bluetoothOff
    case error(String)

    var label: String {
        switch self {
        case .idle: return "Idle"
        case .connecting: return "Connecting…"
        case .connected: return "Connected"
        case .disconnected: return "Disconnected"
        case .bluetoothOff: return "Bluetooth off"
        case .error(let m): return "Error: \(m)"
        }
    }
}

/// Core state machine: maps USB source presence to connect/disconnect of the enabled
/// Bluetooth devices (SPECIFICATION.md §3; the Windows engine mirrors it).
///
/// Work is organized in *runs*. Every automation transition, manual action and the sleep release
/// starts or joins a run; a newer run supersedes older ones (`runToken`), and in-flight work checks
/// `staleReason` after every await, so a superseded run unwinds instead of undoing the newer run's
/// work. Devices with an operation in flight are *busy* (ref-counted), so status events and the
/// poll don't misread transitions as unexpected drops.
///
/// Logging (category `engine`): every run, try, step (with elapsed time), backoff, abort reason,
/// give-up (with the last error), and the handoff timeline (time since the source appeared or
/// disappeared). Failures are classified (`BTFailureKind`) to pick the retry policy.
@MainActor
final class SelectionEngine: ObservableObject {
    @Published private(set) var selected: Bool = false
    @Published private(set) var statuses: [UUID: DeviceStatus] = [:]
    @Published private(set) var bluetoothPowered: Bool = true
    @Published var lastReason: String = "startup"

    /// Optional sink for user-facing notices (title, body) raised on connect/disconnect events.
    var onNotice: ((String, String) -> Void)?
    /// Called when a managed device drops while the source is still present (not an engine action).
    var onUnexpectedDisconnect: ((BTDevice) -> Void)?

    private let store: ConfigStore
    private let usb: USBMonitoring
    private let bt: BluetoothController

    private var cancellables = Set<AnyCancellable>()
    /// Source devices currently attached: one key per bus location when the source is pinned to
    /// locations, otherwise one per product ID (`USBSource.presenceKey`).
    private var presentKeys = Set<String>()
    /// Presence the automation last connected/disconnected for (latch, distinct from `selected`
    /// which tracks raw presence even while paused).
    private var automationActed = false
    private var debounceTimer: Timer?
    /// Bumps on every new run so in-flight work can detect it was superseded.
    private var runToken = 0
    /// Set by `seed()`: the next evaluation that finds the source absent releases whatever this Mac
    /// may still hold (spec §3 Evaluate step 5). Seeding clears `automationActed`, so the normal
    /// "source gone" transition can't fire after a seed.
    private var reconcileOnSeed = false
    /// When the source presence last flipped (present ↔ absent): the start of the handoff timeline.
    private var presenceChangedAt: ContinuousClock.Instant?

    private var statusTimer: Timer?
    /// Devices with an engine operation in flight (ref-counted: a manual action can overlap a
    /// superseded run that hasn't unwound yet). Status events/polls skip busy devices.
    private var busyCounts: [UUID: Int] = [:]
    /// Devices whose most recently requested operation is a release. A pair or connect that
    /// completes after the release was requested (a stale run was mid-ceremony) is undone — or this
    /// Mac would hold the bond while switched away, and the other host could not pair.
    private var releaseRequested = Set<UUID>()
    /// True between `prepareForSleep()` and `resumeAfterWake()`: automation is halted.
    private(set) var suspended = false

    // MARK: Tunables / test seams (internal so tests can make them instant)

    var autoReconnectDelayMs = 2000
    var autoReconnectMinInterval: TimeInterval = 30
    /// Soft failures (device busy / operation in progress) retry on this fixed interval…
    var softRetryIntervalMs = 3000
    /// …up to this many times without spending `connectRetryMax`; then they count as hard.
    var maxSoftRetries = 20
    /// Delay before retrying a failed unpair during a release.
    var releaseRetryDelayMs = 1000
    /// Settle time after dropping the transient post-pair connection.
    var postPairSettleMs = 800
    /// After a pair, how long to wait for bluetoothd to bring HID up on the pairing link itself
    /// before dropping that link and reconnecting.
    var postPairHidWaitMs = 3000
    /// Minimum time between an unpair returning and the next pair of the same device (spec §3;
    /// `AppConfig.postUnpairSettleMs`, user-tunable): the system finishes an unpair up to ~2.6 s
    /// after `-remove` returns, and a pair started earlier failed (HCI 0x02) on hardware.
    var postUnpairSettleMs: Int { max(0, store.config.postUnpairSettleMs) }
    /// How long a connected link may take to bring up HID before the try counts as failed (on
    /// hardware HID came up ~0.5 s after the link; a dead link never did).
    var hidReadyTimeoutMs = 5000
    var hidPollIntervalMs = 250
    /// This many "link up but no HID" failures in one connect run end it early: on hardware the
    /// trackpad stayed wedged (until power-cycled) and more retries only churned it.
    var maxHidFailures = 2
    /// Every engine delay goes through here (connect delay, settle, backoff, soft retry,
    /// auto-reconnect delay) so tests can run retry paths instantly.
    var sleepHook: @MainActor (Int) async -> Void = { ms in
        if ms > 0 { try? await Task.sleep(nanoseconds: UInt64(ms) * 1_000_000) }
    }
    private var lastAutoReconnect: [UUID: Date] = [:]
    /// Unpairs in flight per device (a superseded release keeps running after a new run starts).
    private var unpairsInFlight: [UUID: Int] = [:]
    /// When each device's last unpair returned (its link was down; the key deletion still pending).
    private var lastUnpairAt: [UUID: ContinuousClock.Instant] = [:]

    init(store: ConfigStore, usb: USBMonitoring, bt: BluetoothController, hostSwitcher: HostSwitcher? = nil) {
        self.store = store
        self.usb = usb
        self.bt = bt
        self.hostSwitcher = hostSwitcher

        // Events are delivered on the main queue (USBMonitor dispatches there), so assumeIsolated
        // is safe and lets us call the @MainActor handler from the Combine closure.
        usb.events
            .sink { [weak self] event in MainActor.assumeIsolated { self?.handleUSB(event) } }
            .store(in: &cancellables)

        // Re-register Bluetooth observers whenever the device list changes (debounced so rapid
        // settings edits don't thrash). dropFirst skips the current value (startup registers it).
        // DispatchQueue.main, not RunLoop.main: the run-loop scheduler stalls in tracking mode
        // (menu open / modal panel), which would freeze automation while the UI is interacted with.
        //
        // Spec §3 Evaluate step 6: a device list edit is not a seed. Only the added or re-enabled
        // device connects (`deviceListChanged`); the others are left alone.
        lastDeviceList = DeviceListSnapshot(profileID: store.config.activeProfileID, devices: store.config.devices)
        store.$config
            .map { DeviceListSnapshot(profileID: $0.activeProfileID, devices: $0.devices) }
            .removeDuplicates()
            // Drop the current value *before* debouncing: after the debounce, an edit made within
            // 300 ms of startup would merge with it and be dropped too.
            .dropFirst()
            .debounce(for: .milliseconds(300), scheduler: DispatchQueue.main)
            .sink { [weak self] snapshot in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.refreshMonitoring()
                    let previous = self.lastDeviceList
                    self.lastDeviceList = snapshot
                    Task { @MainActor in await self.deviceListChanged(from: previous, to: snapshot) }
                }
            }
            .store(in: &cancellables)
    }

    /// The device list as the engine last saw it, with the profile it belongs to.
    struct DeviceListSnapshot: Equatable {
        let profileID: UUID
        let devices: [BTDevice]
    }

    private var lastDeviceList: DeviceListSnapshot?

    /// Spec §3 Evaluate step 6. A device that was added or re-enabled while this Mac is selected
    /// (and the automation has acted on that) connects on its own, joining the current run. A
    /// removed device is forgotten and left as it is. Nothing else runs again: a full pass would
    /// put every managed device through pair + HID check and show working devices "Connecting…".
    ///
    /// A profile switch or an import also changes the list, but those re-seed the engine, which
    /// connects everything itself; so this only acts within the same profile and while
    /// `automationActed` holds (a pending re-evaluation clears it). Internal for tests.
    func deviceListChanged(from old: DeviceListSnapshot?, to new: DeviceListSnapshot) async {
        guard let old, old.profileID == new.profileID else { return }
        let oldByID = Dictionary(uniqueKeysWithValues: old.devices.map { ($0.id, $0) })
        let newIDs = Set(new.devices.map(\.id))

        for removed in old.devices where !newIDs.contains(removed.id) {
            statuses[removed.id] = nil
            lastAutoReconnect[removed.id] = nil
            Log.engine.log("\(removed.name): removed from the device list; left as it is")
        }

        let joined = new.devices.filter { device in
            device.enabled && !(oldByID[device.id]?.enabled ?? false)
        }
        for device in joined {
            await connectJoinedDevice(device, added: oldByID[device.id] == nil)
        }
    }

    private func connectJoinedDevice(_ device: BTDevice, added: Bool) async {
        let what = added ? "added" : "re-enabled"
        let skip: String? =
            !selected
            ? "the KVM is on another computer"
            : !automationActed
                ? "a re-evaluation will connect it"
                : store.config.paused
                    ? "automation is paused"
                    : suspended
                        ? "the system is going to sleep"
                        : !bluetoothPowered
                            ? "Bluetooth is off" : isBusy(device.id) ? "another operation is in flight" : nil
        if let skip {
            Log.engine.debug("\(device.name): \(what); not connecting now (\(skip))")
            return
        }
        Log.engine.log("\(device.name): \(what) while the KVM is on this Mac → connecting")
        let run = currentRun("device \(what)")
        markBusy(device.id)
        defer { clearBusy(device.id) }
        await connectOne(device, run: run, respectSelection: true)
    }

    // MARK: - Runs

    /// A unit of engine work: its token, what started it (for the log), whether it may run while
    /// the system is suspending (only the sleep release), and the presence change that triggered it.
    private struct Run {
        let token: Int
        let trigger: String
        var sleepRelease = false
        var since: ContinuousClock.Instant?
    }

    private func newRun(_ trigger: String) -> Run {
        runToken += 1
        return Run(token: runToken, trigger: trigger)
    }

    private func currentRun(_ trigger: String) -> Run {
        Run(token: runToken, trigger: trigger)
    }

    /// Like `staleReason`, for a release: an automatic release also stops when the source is back
    /// (a fast switch-back while it was still running). Before the arrival debounce supersedes the
    /// run, the Mac would otherwise still send a keyboard to the other computer's channel, or unpair
    /// the trackpad, after the KVM had already returned. Manual and sleep releases always finish.
    private func releaseStaleReason(_ run: Run) -> String? {
        if let why = staleReason(run, respectSelection: false) { return why }
        if run.since != nil, !run.sleepRelease, !presentKeys.isEmpty { return "the KVM came back" }
        return nil
    }

    /// Why in-flight work for this run must stop, or nil if it may continue.
    private func staleReason(_ run: Run, respectSelection: Bool) -> String? {
        if suspended && !run.sleepRelease { return "the system is going to sleep" }
        if run.token != runToken { return "superseded by run #\(runToken)" }
        if respectSelection && !selected { return "the source is no longer present" }
        return nil
    }

    // MARK: - Status monitoring

    /// Starts event-driven status updates (IOBluetooth notifications) plus a slow poll as a
    /// safety net in case an event is ever missed.
    func startStatusMonitor(pollIntervalSeconds: Double = 10.0) {
        refreshMonitoring()
        statusTimer?.invalidate()
        // .common mode so the poll keeps running while a menu or modal panel is open
        // (.default-mode timers stall during run-loop tracking).
        let timer = Timer(timeInterval: pollIntervalSeconds, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.pollStatuses() }
        }
        RunLoop.main.add(timer, forMode: .common)
        statusTimer = timer
        Task { @MainActor in await pollStatuses() }
    }

    /// (Re)register IOBluetooth connect/disconnect observers for the current device set.
    func refreshMonitoring() {
        let addresses = store.config.devices.map { $0.normalizedAddress }
        bt.startMonitoring(addresses: addresses) { [weak self] addr, connected in
            self?.handleStatusEvent(addr: addr, connected: connected)
        }
    }

    private func handleStatusEvent(addr: String, connected: Bool) {
        guard let device = store.config.devices.first(where: { $0.normalizedAddress == addr }) else { return }
        guard !isBusy(device.id) else { return }
        updateStatus(device, connected: connected, origin: "event")
    }

    private func pollStatuses() async {
        await refreshPower()
        for device in store.config.devices {
            guard !isBusy(device.id) else { continue }
            if !bluetoothPowered {
                setStatus(device.id, .bluetoothOff)
                continue
            }
            let connected = await bt.isConnected(device.normalizedAddress)
            guard !isBusy(device.id) else { continue }  // an operation started while we were asking
            updateStatus(device, connected: connected, origin: "poll")
        }
    }

    /// Reflect a device's live connection state, and flag a passive drop (connected → disconnected
    /// while the source is present, BT is on, and we're not mid-transition) as unexpected.
    private func updateStatus(_ device: BTDevice, connected: Bool, origin: String) {
        let old = statuses[device.id]
        let new: DeviceStatus = connected ? .connected : .disconnected
        if old != new {
            Log.engine.debug(
                old.map { "\(device.name): \($0.label) → \(new.label) (reported by \(origin))" }
                    ?? "\(device.name): initial status \(new.label) (\(origin))")
            setStatus(device.id, new)
        }

        if !connected, old == .connected, selected, bluetoothPowered, device.enabled, !suspended {
            Log.engine.log("\(device.name): dropped unexpectedly while the source is present")
            onUnexpectedDisconnect?(device)
            maybeAutoReconnect(device)
        }
    }

    /// One bounded, rate-limited reconnect after an unexpected drop (opt-out via
    /// `autoReconnectOnDrop`). Joins the current run, so any newer run supersedes it; combined
    /// with the stale-bond re-pair on retry this self-heals even when the bond went stale.
    private func maybeAutoReconnect(_ device: BTDevice) {
        guard store.config.autoReconnectOnDrop else { return }
        if store.config.paused || suspended {
            Log.engine.debug(
                "\(device.name): no auto-reconnect (\(suspended ? "going to sleep" : "automation paused"))")
            return
        }
        guard !isBusy(device.id) else { return }
        let now = Date()
        if let last = lastAutoReconnect[device.id], now.timeIntervalSince(last) < autoReconnectMinInterval {
            let ago = Int(now.timeIntervalSince(last))
            Log.engine.debug("\(device.name): auto-reconnect skipped (rate limit: last one \(ago) s ago)")
            return
        }
        lastAutoReconnect[device.id] = now
        Log.engine.debug("\(device.name): auto-reconnect scheduled in \(autoReconnectDelayMs) ms")

        let run = currentRun("auto-reconnect after drop")
        Task { @MainActor [weak self] in
            guard let self else { return }
            await self.sleepHook(self.autoReconnectDelayMs)
            let skip =
                self.staleReason(run, respectSelection: true)
                ?? (!self.bluetoothPowered
                    ? "Bluetooth is off"
                    : self.store.config.paused
                        ? "automation paused" : self.isBusy(device.id) ? "another operation is in flight" : nil)
            if let skip {
                Log.engine.debug("\(device.name): auto-reconnect cancelled: \(skip)")
                return
            }
            self.markBusy(device.id)
            defer { self.clearBusy(device.id) }
            await self.connectOne(device, run: run, respectSelection: true)
        }
    }

    /// Refresh adapter power; on a false→true transition, re-evaluate so devices reconnect.
    /// Internal so tests can drive it.
    func refreshPower() async {
        let powered = await bt.isPoweredOn() ?? true
        guard powered != bluetoothPowered else { return }
        bluetoothPowered = powered
        if powered {
            Log.engine.log("Bluetooth powered on → re-evaluating")
            seed()
        } else {
            Log.engine.log("Bluetooth powered off: connects are skipped until it is back on")
        }
    }

    // MARK: - System sleep / wake

    /// The system is about to sleep: halt automation, supersede in-flight work, and release the
    /// enabled devices (disconnect, plus unpair for managePairing devices) so the other host can
    /// take them while this Mac sleeps. Goes through the same busy/status bookkeeping as an
    /// automation release, so the stack's late "disconnected" events can't look like unexpected
    /// drops and trigger an auto-reconnect during sleep. `SleepWakeMonitor` holds the sleep
    /// transition (bounded) until this returns.
    func prepareForSleep() async {
        suspended = true
        debounceTimer?.invalidate()
        debounceTimer = nil
        var run = newRun("system sleep")
        run.sleepRelease = true
        let devices = store.config.devices.filter(\.enabled)
        let start = ContinuousClock.now
        Log.engine.log(
            "run #\(run.token): system sleep → automation halted; releasing \(devices.count) enabled device(s)")
        for device in devices {
            if let why = staleReason(run, respectSelection: false) {
                Log.engine.debug("run #\(run.token): sleep release stopped: \(why)")
                return
            }
            markBusy(device.id)
            defer { clearBusy(device.id) }
            await disconnectOne(device, run: run)
        }
        // Devices are released; on wake, seeding re-evaluates from scratch.
        automationActed = false
        Log.engine.log("run #\(run.token): sleep release finished in \(Elapsed.since(start))")
    }

    /// The system woke: re-enable automation and re-seed presence from the current USB snapshot
    /// (call `usb.refreshAttached()` first).
    func resumeAfterWake() {
        if suspended {
            suspended = false
            Log.engine.log("system woke → automation re-enabled; re-seeding")
        }
        seed()
    }

    // MARK: - Seeding & USB input

    /// Called at launch and on wake to establish current presence from scratch.
    func seed() {
        let source = store.config.source
        presentKeys.removeAll()
        automationActed = false  // re-evaluate from scratch (covers profile switches and wake)
        reconcileOnSeed = true
        if let source {
            for dev in usb.attachedDevices where source.matches(dev) {
                presentKeys.insert(source.presenceKey(productID: dev.productID, locationID: dev.locationID))
            }
        }
        lastReason = "initial scan"
        presenceChangedAt = .now
        if let source {
            let sameIDs = usb.attachedDevices.filter {
                $0.vendorID == source.vendorID && source.productIDs.contains($0.productID)
            }
            Log.engine.log(
                "seed: source '\(source.name)' \(presentKeys.isEmpty ? "absent" : "present") "
                    + "(matching: \(presentKeys.sorted().joined(separator: ", ")); devices with the source IDs: "
                    + "\(sameIDs.map(\.displayName).joined(separator: "; ")))")
            if source.locationIDs?.isEmpty ?? true, Set(sameIDs.map(\.productID)).count < sameIDs.count {
                Log.engine.warn(
                    "seed: the source IDs match \(sameIDs.count) USB devices at different bus locations, so the "
                        + "source can't tell the KVM's switching hub from a permanent one (e.g. a dock) and may "
                        + "count as present while the KVM is on the other host. Re-learn the source "
                        + "(Settings ▸ Source) to pin it to the hub's location.")
            }
        } else {
            Log.engine.log("seed: no source configured")
        }
        scheduleEvaluate()
    }

    /// Test convenience: an event without a bus location.
    func handleUSB(_ event: (vendorID: UInt16, productID: UInt16, added: Bool)) {
        handleUSB(USBEvent(vendorID: event.vendorID, productID: event.productID, locationID: nil, added: event.added))
    }

    /// Internal (not private) so unit tests can simulate USB events directly.
    func handleUSB(_ event: USBEvent) {
        guard let source = store.config.source,
            source.matches(vendorID: event.vendorID, productID: event.productID, locationID: event.locationID)
        else { return }

        let wasPresent = !presentKeys.isEmpty
        let key = source.presenceKey(productID: event.productID, locationID: event.locationID)
        let id =
            String(format: "0x%04X:0x%04X", event.vendorID, event.productID)
            + (event.locationID.map { String(format: " @ 0x%08X", $0) } ?? "")
        if event.added {
            presentKeys.insert(key)
            lastReason = String(format: "source arrived (0x%04X)", event.productID)
        } else {
            presentKeys.remove(key)
            lastReason = String(format: "source removed (0x%04X)", event.productID)
        }
        let isPresent = !presentKeys.isEmpty
        // The handoff timeline starts here: the Windows side measures the Mac's release from it.
        if isPresent != wasPresent {
            presenceChangedAt = .now
            Log.usb.log(
                isPresent
                    ? "source '\(source.name)' appeared (\(id) attached); arrival debounce \(store.config.arrivalDebounceMs) ms"
                    : "source '\(source.name)' gone (\(id) detached, no source devices left); departure debounce \(store.config.debounceMs) ms"
            )
        } else {
            Log.usb.debug(
                "source device \(id) \(event.added ? "attached" : "detached") "
                    + "(\(presentKeys.count) source device(s) present)")
        }
        scheduleEvaluate()
    }

    private func scheduleEvaluate() {
        debounceTimer?.invalidate()
        // Source present now → arrival debounce (snappier connect); gone → departure debounce.
        let ms = presentKeys.isEmpty ? store.config.debounceMs : store.config.arrivalDebounceMs
        let interval = Double(ms) / 1000.0
        // .common mode: the handoff must fire even while the user has a menu or panel open.
        let timer = Timer(timeInterval: interval, repeats: false) { [weak self] _ in
            Task { @MainActor in self?.evaluate() }
        }
        RunLoop.main.add(timer, forMode: .common)
        debounceTimer = timer
    }

    private func evaluate() {
        Task { @MainActor in await self.evaluateNow() }
    }

    /// The transition logic, awaitable so tests can drive it deterministically without timers.
    /// Internal for testing; production reaches it via the debounce timer in `evaluate()`.
    func evaluateNow() async {
        let present = !presentKeys.isEmpty
        selected = present  // always reflect real presence for the UI

        let wouldAct = present != automationActed || (!present && reconcileOnSeed)
        if suspended {
            if wouldAct { Log.engine.debug("evaluate: going to sleep; not acting on '\(lastReason)'") }
            return
        }
        guard !store.config.paused else {  // automation suspended; don't act
            if wouldAct { Log.engine.debug("evaluate: automation paused; not acting on '\(lastReason)'") }
            return
        }
        presenceEvaluated = true

        // `automationActed` latches the presence the automation last acted on, so resuming from
        // pause reconciles to the current state.
        let since = presenceChangedAt.map { " \(Elapsed.since($0)) ago" } ?? ""
        let enabled = store.config.devices.filter(\.enabled).count
        if present { reconcileOnSeed = false }  // the connect transition covers it
        if present && !automationActed {
            automationActed = true
            var run = newRun("auto: \(lastReason)")
            run.since = presenceChangedAt
            Log.engine.log(
                "run #\(run.token): source present (\(lastReason)\(since)) → connecting \(enabled) device(s)")
            await connectAll(run: run)
        } else if !present && automationActed {
            automationActed = false
            reconcileOnSeed = false
            var run = newRun("auto: \(lastReason)")
            run.since = presenceChangedAt
            Log.engine.log("run #\(run.token): source gone (\(lastReason)\(since)) → releasing \(enabled) device(s)")
            await disconnectAll(run: run)
        } else if !present && reconcileOnSeed {
            reconcileOnSeed = false
            let run = newRun("seed: source absent")
            Log.engine.log(
                "run #\(run.token): source absent after a seed (\(lastReason)) → "
                    + "releasing anything this Mac still holds")
            await releaseHeld(run: run)
        }
    }

    /// Spec §3 Evaluate step 5: after a seed with the source absent (launch while switched away,
    /// unpause or wake after the KVM moved, profile switch), release enabled devices this Mac still
    /// holds: disconnect connected ones, unpair bonded managePairing ones. Devices not held are
    /// skipped; a notice is posted only for a device that was actually connected.
    private func releaseHeld(run: Run) async {
        let pairedList = await bt.pairedDevices()
        let paired = Set(pairedList.map { $0.address.replacingOccurrences(of: ":", with: "-").lowercased() })
        for device in store.config.devices where device.enabled {
            if let why = staleReason(run, respectSelection: false) {
                Log.engine.debug("run #\(run.token): seed release stopped: \(why)")
                return
            }
            let connected = await bt.isConnected(device.normalizedAddress)
            let bonded = device.managePairing && paired.contains(device.normalizedAddress)
            guard connected || bonded else {
                Log.engine.debug("\(device.name): not held by this Mac; nothing to release")
                continue
            }
            markBusy(device.id)
            defer { clearBusy(device.id) }
            await disconnectOne(device, run: run, notify: connected)
        }
    }

    // MARK: - Manual actions (ignore selection; supersede in-flight automation)

    func connectAllNow() { Task { @MainActor in await connectAllNowImpl() } }
    func disconnectAllNow() { Task { @MainActor in await disconnectAllNowImpl() } }

    func connectAllNowImpl() async {
        let run = newRun("manual: connect all")
        Log.engine.log("run #\(run.token): manual connect all (\(store.config.devices.count) device(s))")
        for device in store.config.devices {
            if let why = staleReason(run, respectSelection: false) {
                Log.engine.debug("run #\(run.token): connect all stopped: \(why)")
                return
            }
            markBusy(device.id)
            defer { clearBusy(device.id) }
            await connectOne(device, run: run, respectSelection: false)
        }
    }

    func disconnectAllNowImpl() async {
        let run = newRun("manual: disconnect all")  // invalidates any in-flight connect run
        Log.engine.log("run #\(run.token): manual disconnect all (\(store.config.devices.count) device(s))")
        for device in store.config.devices {
            if let why = staleReason(run, respectSelection: false) {
                Log.engine.debug("run #\(run.token): disconnect all stopped: \(why)")
                return
            }
            markBusy(device.id)
            defer { clearBusy(device.id) }
            await disconnectOne(device, run: run)
        }
    }

    /// Per-device manual connect (Settings ▸ Connect): the full connect flow, retries included,
    /// ignoring selection.
    func connectDeviceNow(_ device: BTDevice) async {
        let run = beginManual(device, "manual connect")
        markBusy(device.id)
        defer { clearBusy(device.id) }
        await connectOne(device, run: run, respectSelection: false)
    }

    /// Raw single disconnect (Settings ▸ Disconnect test button). Returns the error, or nil.
    @discardableResult
    func disconnectDeviceNow(_ device: BTDevice) async -> String? {
        let addr = device.normalizedAddress
        return await rawOp(device, "disconnect", releases: true) { try await self.bt.disconnect(addr) }
    }

    /// Raw single pair (Settings ▸ Pair test button). Returns the error, or nil.
    @discardableResult
    func pairDeviceNow(_ device: BTDevice) async -> String? {
        let addr = device.normalizedAddress
        return await rawOp(device, "pair", releases: false) { try await self.bt.pair(addr) }
    }

    /// Raw single unpair (Settings ▸ Unpair test button). Returns the error, or nil.
    @discardableResult
    func unpairDeviceNow(_ device: BTDevice) async -> String? {
        let addr = device.normalizedAddress
        return await rawOp(device, "unpair", releases: true) { try await self.unpairTracked(device.id, addr) }
    }

    /// A manual action on a device the engine is already working on supersedes that work (the
    /// user took control). Otherwise it joins the current run, so a later automation run (e.g. the
    /// source leaving) still supersedes it.
    private func beginManual(_ device: BTDevice, _ what: String) -> Run {
        if isBusy(device.id) {
            let run = newRun(what)
            Log.engine.debug("\(device.name): \(what) supersedes the in-flight operation (run #\(run.token))")
            return run
        }
        return currentRun(what)
    }

    private func rawOp(
        _ device: BTDevice, _ op: String, releases: Bool, _ call: @escaping @Sendable () async throws -> Void
    ) async -> String? {
        _ = beginManual(device, "manual \(op)")
        markBusy(device.id)
        defer { clearBusy(device.id) }
        if releases { releaseRequested.insert(device.id) } else { releaseRequested.remove(device.id) }

        let start = ContinuousClock.now
        Log.engine.debug("\(device.name): manual \(op) started")
        do {
            try await withTimeout(store.config.btCallTimeoutSecs, call)
        } catch {
            let failure = Failure(error)
            Log.engine.warn("\(device.name): manual \(op) failed after \(Elapsed.since(start)): \(failure.summary)")
            setStatus(device.id, .error(failure.message))
            return failure.message
        }
        Log.engine.log("\(device.name): manual \(op) OK (\(Elapsed.since(start)))")
        // Set the status explicitly while still busy: the stack's late "disconnected" event then
        // matches it instead of looking like an unexpected drop (which would trigger an
        // auto-reconnect that undoes the user's unpair/disconnect).
        if releases {
            setStatus(device.id, .disconnected)
        } else {
            setStatus(device.id, await bt.isConnected(device.normalizedAddress) ? .connected : .disconnected)
        }
        return nil
    }

    // MARK: - Retry backoff

    private static let maxBackoffSeconds = 30.0

    /// Backoff (seconds) between connect retries: `base * 2^(attempt-1)`, capped. With the default
    /// base of 2s and a 1-based `attempt`, this is 2, 4, 8, 16, 30… Deterministic.
    static func backoffSeconds(base: Int, attempt: Int) -> Double {
        let value = Double(base) * pow(2.0, Double(attempt) - 1.0)
        return min(value, maxBackoffSeconds)
    }

    /// Backoff with ±15% jitter, in nanoseconds (used between connect retries). The 30 s cap
    /// applies after jitter — the spec's cap bounds the real sleep.
    static func backoffNanos(base: Int, attempt: Int) -> UInt64 {
        let jittered = backoffSeconds(base: base, attempt: attempt) * Double.random(in: 0.85...1.15)
        let capped = min(jittered, Self.maxBackoffSeconds)
        return UInt64(max(0, capped) * 1_000_000_000)
    }

    // MARK: - Connect

    private func connectAll(run: Run) async {
        let devices = store.config.devices.filter(\.enabled)
        // A channel-switching device comes over by itself and its wait uses no radio, so it runs
        // alongside the others: a slow pair (17 s on hardware, in a fast switch-back) mustn't keep
        // it waiting in line.
        var waits: [Task<Void, Never>] = []
        for device in devices where switchesChannels(device) {
            markBusy(device.id)
            waits.append(
                Task { @MainActor [weak self] in
                    guard let self else { return }
                    defer { self.clearBusy(device.id) }
                    await self.connectOne(device, run: run, respectSelection: true)
                })
        }
        for device in devices where !switchesChannels(device) {
            if let why = staleReason(run, respectSelection: false) {
                Log.engine.debug("run #\(run.token): connect run stopped: \(why)")
                break
            }
            markBusy(device.id)
            defer { clearBusy(device.id) }
            await connectOne(device, run: run, respectSelection: true)
        }
        for wait in waits { await wait.value }
    }

    /// The connect flow for one device (SPECIFICATION.md §3), with failure classification:
    /// - Soft failures (device busy / operation in progress) retry every `softRetryIntervalMs` and
    ///   don't consume `connectRetryMax` (bounded by `maxSoftRetries`).
    /// - Hard failures consume an attempt and back off exponentially. For managePairing devices, a
    ///   hard failure that may mean a stale bond (authentication / timeout / unknown) makes the next
    ///   try unpair first and re-pair from scratch.
    /// - An invalid address gives up immediately; an unavailable adapter refreshes power first.
    /// - Staleness is re-checked after every step; a pair/connect that completes after a release
    ///   was requested is undone.
    private func connectOne(_ device: BTDevice, run: Run, respectSelection: Bool) async {
        let cfg = store.config
        let addr = device.normalizedAddress
        let name = device.name
        let maxHard = max(1, cfg.connectRetryMax)
        let start = ContinuousClock.now
        releaseRequested.remove(device.id)

        Log.engine.debug(
            "\(name): connect started (run #\(run.token), \(run.trigger); managePairing=\(device.managePairing))")
        setStatus(device.id, .connecting)

        // Optional per-device stagger before connecting.
        if device.connectDelayMs > 0 {
            await sleepHook(device.connectDelayMs)
            if let why = staleReason(run, respectSelection: respectSelection) {
                Log.engine.debug(
                    "\(name): connect aborted during the \(device.connectDelayMs) ms connect delay: \(why)")
                markAborted(device.id)
                return
            }
        }

        // Spec §5a type 5: a device that switches channel on request comes over by itself when the
        // other computer sends it here. Calling it meanwhile only gets in its way (seen on
        // hardware: three page timeouts kept a K380 out for ~20 s), so wait for it instead.
        if switchesChannels(device) {
            await waitForChannelArrival(device, run: run, respectSelection: respectSelection, start: start)
            return
        }

        var tries = 0
        var hardFailures = 0
        var softRetries = 0
        var hidFailures = 0
        var freshBond = false
        var last: Failure?

        while true {
            if !bluetoothPowered {
                Log.engine.debug("\(name): connect stopped: Bluetooth is off")
                setStatus(device.id, .bluetoothOff)
                return
            }
            if let why = staleReason(run, respectSelection: respectSelection) {
                Log.engine.debug("\(name): connect aborted before try \(tries + 1): \(why)")
                markAborted(device.id)
                return
            }
            tries += 1
            let tryStart = ContinuousClock.now
            var step = "status check"
            var failure: Failure?
            var hidOnPairingLink = false

            do {
                if !device.managePairing, await bt.isConnected(addr), await bt.isHIDReady(addr) != false {
                    Log.engine.log("\(name): already connected")
                    setStatus(device.id, .connected)
                    return
                }
                if device.managePairing {
                    if freshBond {
                        freshBond = false  // once per hard failure, not again on each soft retry
                        Log.engine.warn("\(name): try \(tries): removing a possibly stale bond before re-pairing")
                        await bestEffort(device, "stale-bond unpair") { try await self.unpairTracked(device.id, addr) }
                        await waitForUnpairToSettle(device)
                        if let why = staleReason(run, respectSelection: respectSelection) {
                            Log.engine.debug("\(name): connect aborted after the stale-bond unpair: \(why)")
                            markAborted(device.id)
                            return
                        }
                    }
                    // A quick KVM bounce starts this run while the superseded release is still
                    // unpairing; bluetoothd also deletes the old key ~1.5 s after the link drops.
                    // A pair started inside that window collides with the unpair.
                    await waitForUnpairToSettle(device)
                    if let why = staleReason(run, respectSelection: respectSelection) {
                        Log.engine.debug("\(name): connect aborted while an unpair was settling: \(why)")
                        markAborted(device.id)
                        return
                    }
                    step = "pair"
                    try await self.step(name, tries, "pair") { try await self.bt.pair(addr) }
                    if let why = staleReason(run, respectSelection: respectSelection) {
                        Log.engine.debug("\(name): connect aborted after pairing: \(why)")
                        await undoIfReleased(device, why: "a pair completed")
                        markAborted(device.id)
                        return
                    }
                    // bluetoothd brings HID up on the pairing link by itself. Tearing that link
                    // down right after pairing made the system re-page the trackpad at once, and
                    // the HID it opened on that link was refused (STATUS 706) and stayed refused
                    // (hardware, 2026-11-12). So wait for HID first, and reconnect only without it.
                    if await hidUpAfterPair(device, addr) {
                        hidOnPairingLink = true
                    } else {
                        await bestEffort(device, "post-pair disconnect") { try await self.bt.disconnect(addr) }
                        await sleepHook(postPairSettleMs)
                        if let why = staleReason(run, respectSelection: respectSelection) {
                            Log.engine.debug("\(name): connect aborted after the post-pair settle: \(why)")
                            await undoIfReleased(device, why: "a pair completed")
                            markAborted(device.id)
                            return
                        }
                    }
                }
                if !hidOnPairingLink {
                    step = "connect"
                    try await self.step(name, tries, "openConnection") { try await self.bt.connect(addr) }
                    step = "verify"
                    // A clean connect is necessary but not sufficient: isConnected must agree.
                    if !(await bt.isConnected(addr)) {
                        failure = Failure(.unknown, "openConnection succeeded but the device does not report connected")
                    }
                }
            } catch {
                let f = Failure(error)
                if step == "connect", f.kind == .timeout, await bt.isConnected(addr) {
                    // openConnection can complete just after the deadline (withTimeout abandons it
                    // rather than waiting). A connection that arrived late is a success.
                    Log.engine.warn(
                        "\(name): try \(tries): openConnection overran its timeout but the device is connected; "
                            + "keeping it")
                } else {
                    failure = f
                }
            }

            // A link alone is not success: on hardware, a 1 ms openConnection on a bare link
            // reported "connected" while HID never came up (the link idled out 15 s later).
            if failure == nil, let hidUp = await waitForHID(device, addr), !hidUp {
                failure = Failure(BTError.hidNotReady(seconds: Double(hidReadyTimeoutMs) / 1000))
            }

            guard let f = failure else {
                // Connected. If a release was requested meanwhile (source left, sleep, manual
                // release), the connection must not stand.
                if let why = staleReason(run, respectSelection: respectSelection) {
                    if releaseRequested.contains(device.id) || suspended {
                        Log.engine.warn("\(name): connected after the run was superseded (\(why)); releasing it again")
                        await undoIfReleased(device, why: "a connection completed")
                    } else {
                        Log.engine.debug(
                            "\(name): connected, but the run was superseded (\(why)); leaving it to the newer run")
                    }
                    markAborted(device.id)
                    return
                }
                let timeline = run.since.map { "; \(Elapsed.since($0)) after the source appeared" } ?? ""
                Log.engine.log(
                    "\(name): connected ✓ after \(tries) \(tries == 1 ? "try" : "tries") in \(Elapsed.since(start))\(timeline)"
                )
                setStatus(device.id, .connected)
                onNotice?("Connected", name)
                return
            }

            last = f
            // Expected refusals (the other host still holds it, an operation in flight) are routine
            // on every switch: debug. Hard failures are warnings (spec §10).
            let tryFailed = "\(name): try \(tries): \(step) failed after \(Elapsed.since(tryStart)): \(f.summary)"
            if f.kind.isSoft { Log.engine.debug(tryFailed) } else { Log.engine.warn(tryFailed) }
            setStatus(device.id, .error(f.message))

            // Don't trust a link-only state; drop any partial connection so the next try is clean.
            if await bt.isConnected(addr) {
                Log.engine.debug(
                    "\(name): the device reports connected despite the failed \(step); dropping it as a partial link")
            }
            await bestEffort(device, "cleanup disconnect") { try await self.bt.disconnect(addr) }

            // Superseded while the try was running: stop here rather than log a misleading retry.
            if let why = staleReason(run, respectSelection: respectSelection) {
                Log.engine.debug("\(name): connect aborted after failed try \(tries): \(why)")
                await undoIfReleased(device, why: "a failed try may have left a bond")
                markAborted(device.id)
                return
            }

            if f.kind == .invalidAddress { break }  // retrying can never help
            if f.kind == .hidNotReady {
                hidFailures += 1
                if hidFailures >= max(1, maxHidFailures) { break }  // wedged: more tries only churn it
            }
            if f.kind == .adapterUnavailable { await refreshPower() }  // loop top stops if it's off

            if f.kind.isSoft {
                if softRetries < maxSoftRetries {
                    softRetries += 1
                    Log.engine.debug(
                        "\(name): \(f.kind == .deviceBusy ? "device refused (most likely still held by the other host)" : "a previous Bluetooth operation is still running"); "
                            + "retrying in \(softRetryIntervalMs) ms (soft retry \(softRetries)/\(maxSoftRetries), "
                            + "not counted against the \(maxHard) attempts)")
                    await sleepHook(softRetryIntervalMs)
                    continue
                }
                Log.engine.warn("\(name): soft-retry budget (\(maxSoftRetries)) exhausted; counting failures as hard")
            }

            hardFailures += 1
            if hardFailures >= maxHard { break }
            freshBond = device.managePairing && f.kind.mayBeStaleBond
            let backoffMs = Int(Self.backoffNanos(base: cfg.connectRetrySecs, attempt: hardFailures) / 1_000_000)
            Log.engine.debug(
                "\(name): attempt \(hardFailures)/\(maxHard) failed; retrying in \(backoffMs) ms"
                    + (freshBond ? " (will re-pair from scratch)" : ""))
            await sleepHook(backoffMs)
        }

        let wedged = hidFailures >= max(1, maxHidFailures)
        let cause =
            wedged
            ? "it paired but never started its HID connection (\(hidFailures) times); it may be stuck. "
                + "Switch the KVM to the other computer and back, or power-cycle it (switch it off and on)"
            : (last?.message ?? "unknown error")
        let triesText = "\(tries) \(tries == 1 ? "try" : "tries")"
        Log.engine.error(
            "\(name): gave up after \(triesText) in \(Elapsed.since(start)) "
                + "(\(hardFailures) hard, \(softRetries) soft); last error: \(last?.summary ?? cause)")
        setStatus(device.id, .error("gave up after \(triesText): \(cause)"))
        onNotice?("Connection failed", "\(name): gave up after \(triesText) — \(cause)")
    }

    /// Wait (bounded) until the device's HID connection is up. Returns true/false, or nil when the
    /// backend can't tell (then the link alone counts, as before).
    private func waitForHID(_ device: BTDevice, _ addr: String) async -> Bool? {
        let start = ContinuousClock.now
        let polls = max(1, hidReadyTimeoutMs / max(1, hidPollIntervalMs))
        for poll in 0...polls {
            guard let ready = await bt.isHIDReady(addr) else { return nil }
            if ready {
                if poll > 0 { Log.engine.debug("\(device.name): HID up after \(Elapsed.since(start))") }
                return true
            }
            if poll == polls { break }
            if !(await bt.isConnected(addr)) {
                Log.engine.debug("\(device.name): the link dropped before HID came up (\(Elapsed.since(start)))")
                return false
            }
            await sleepHook(hidPollIntervalMs)
        }
        Log.engine.debug("\(device.name): link up but no HID after \(Elapsed.since(start))")
        return false
    }

    /// Right after a pair: wait (bounded by `postPairHidWaitMs`) for the HID connection bluetoothd
    /// starts on the pairing link. False when it doesn't come, the link closes, or the backend
    /// can't tell; the caller then drops the link and reconnects.
    private func hidUpAfterPair(_ device: BTDevice, _ addr: String) async -> Bool {
        let start = ContinuousClock.now
        let polls = max(1, postPairHidWaitMs / max(1, hidPollIntervalMs))
        for poll in 0...polls {
            guard let ready = await bt.isHIDReady(addr) else { return false }
            if ready {
                Log.engine.debug("\(device.name): HID up on the pairing link after \(Elapsed.since(start))")
                return true
            }
            if poll == polls { break }
            if !(await bt.isConnected(addr)) {
                if poll > 0 {
                    Log.engine.debug(
                        "\(device.name): the pairing link closed without HID (\(Elapsed.since(start))); reconnecting")
                }
                return false
            }
            await sleepHook(hidPollIntervalMs)
        }
        Log.engine.debug("\(device.name): no HID on the pairing link after \(Elapsed.since(start)); reconnecting")
        return false
    }

    /// Every engine unpair goes through here, so a pair can wait for it (`waitForUnpairToSettle`).
    private func unpairTracked(_ id: UUID, _ addr: String) async throws {
        unpairsInFlight[id, default: 0] += 1
        defer {
            let n = unpairsInFlight[id] ?? 1
            unpairsInFlight[id] = n <= 1 ? nil : n - 1
            lastUnpairAt[id] = .now
        }
        try await bt.unpair(addr)
    }

    /// Before pairing: wait for an unpair of this device still in flight (bounded), then until
    /// `postUnpairSettleMs` has passed since the last unpair returned. A no-op unless a release
    /// happened in the last ~2 s (a KVM bounce, or a release run superseded mid-unpair).
    private func waitForUnpairToSettle(_ device: BTDevice) async {
        let start = ContinuousClock.now
        if unpairsInFlight[device.id] != nil {
            Log.engine.debug("\(device.name): an unpair of this device is still running; waiting before pairing")
            while unpairsInFlight[device.id] != nil, start.duration(to: .now) < .seconds(6) {
                try? await Task.sleep(nanoseconds: 50_000_000)
            }
        }
        guard let last = lastUnpairAt[device.id] else { return }
        let since = last.duration(to: .now)
        let sinceMs = Int(since.components.seconds * 1000 + since.components.attoseconds / 1_000_000_000_000_000)
        let remaining = postUnpairSettleMs - sinceMs
        guard remaining > 0 else { return }
        Log.engine.debug(
            "\(device.name): unpaired \(sinceMs) ms ago; waiting \(remaining) ms for the system to finish "
                + "removing the old bond before pairing")
        await sleepHook(remaining)
    }

    /// If a release was requested for this device (or the system is going to sleep) while a stale
    /// connect run was still working, remove whatever bond/connection that run left behind.
    private func undoIfReleased(_ device: BTDevice, why: String) async {
        guard releaseRequested.contains(device.id) || suspended else { return }
        let addr = device.normalizedAddress
        Log.engine.warn("\(device.name): \(why) after a release was requested; releasing it again")
        await bestEffort(device, "late-release disconnect") { try await self.bt.disconnect(addr) }
        if device.managePairing {
            await bestEffort(device, "late-release unpair") { try await self.unpairTracked(device.id, addr) }
        }
    }

    // MARK: - Release

    private func disconnectAll(run: Run) async {
        for device in store.config.devices where device.enabled {
            if let why = releaseStaleReason(run) {  // e.g. the source came back
                Log.engine.debug("run #\(run.token): release run stopped: \(why)")
                return
            }
            markBusy(device.id)
            defer { clearBusy(device.id) }
            await disconnectOne(device, run: run)
        }
    }

    /// Tear down one device: disconnect, then (managePairing) unpair. Staleness is re-checked
    /// after every await — a newer run owns the devices once it starts, and a stale release must
    /// not undo the connect/pair the new run just performed. A failed unpair is retried once: the
    /// bond is what blocks the other host, so a silent release failure strands the device.
    private func disconnectOne(_ device: BTDevice, run: Run, notify: Bool = true) async {
        let addr = device.normalizedAddress
        let name = device.name
        let start = ContinuousClock.now
        releaseRequested.insert(device.id)
        Log.engine.debug(
            "\(name): release started (run #\(run.token), \(run.trigger); managePairing=\(device.managePairing))")

        if let why = releaseStaleReason(run) {
            Log.engine.log("\(name): release abandoned before it started: \(why)")
            return
        }

        // Spec §5a type 5: send a channel-switching device to the other computer's channel; its
        // pairings stay as they are. Falls through to the usual release when that isn't possible.
        if !device.managePairing, await trySwitchChannel(device, why: "the KVM left this Mac") {
            setStatus(device.id, .disconnected)
            if notify { onNotice?("Switched", "\(name) went to the other computer") }
            return
        }

        var disconnectFailure: Failure?
        do {
            try await step(name, nil, "disconnect") { try await self.bt.disconnect(addr) }
        } catch {
            disconnectFailure = Failure(error)
            Log.engine.warn(
                "\(name): disconnect failed: \(disconnectFailure!.summary)"
                    + (device.managePairing ? "; unpairing anyway" : ""))
        }
        if let why = releaseStaleReason(run) {
            Log.engine.debug("\(name): release abandoned after disconnect: \(why)")
            return
        }

        // Evaluated when logged: the release ends after the unpair, not after the disconnect.
        func timeline() -> String {
            run.since.map { "; \(Elapsed.since($0)) after the source disappeared" } ?? ""
        }

        // Release the bond even if the disconnect call failed — the bond is what blocks the
        // other host (Magic Trackpad), and unpair tears the link down anyway.
        if device.managePairing {
            var unpairFailure: Failure?
            for attempt in 1...2 {
                do {
                    try await step(name, nil, "unpair") { try await self.unpairTracked(device.id, addr) }
                    unpairFailure = nil
                    break
                } catch {
                    let f = Failure(error)
                    unpairFailure = f
                    if let why = releaseStaleReason(run) {
                        Log.engine.debug("\(name): release abandoned after a failed unpair (\(f.summary)): \(why)")
                        return
                    }
                    guard attempt == 1, f.kind != .invalidAddress else { break }
                    Log.engine.warn("\(name): unpair failed: \(f.summary); retrying once in \(releaseRetryDelayMs) ms")
                    await sleepHook(releaseRetryDelayMs)
                    if let why = releaseStaleReason(run) {
                        Log.engine.debug("\(name): release abandoned before the unpair retry: \(why)")
                        return
                    }
                }
            }
            if let why = releaseStaleReason(run) {
                Log.engine.debug("\(name): release abandoned after unpair: \(why)")
                return
            }
            if let f = unpairFailure {
                Log.engine.error(
                    "\(name): release FAILED after \(Elapsed.since(start)): \(f.summary) — the device may still "
                        + "be bonded to this Mac, so the other host cannot take it")
                setStatus(device.id, .error(f.message))
                onNotice?("Release failed", "\(name): \(f.message)")
                return
            }
            Log.engine.log("\(name): released (unpaired) in \(Elapsed.since(start))\(timeline())")
            setStatus(device.id, .disconnected)
            if notify { onNotice?("Disconnected", name) }
            return
        }

        if let f = disconnectFailure {
            Log.engine.error("\(name): release FAILED after \(Elapsed.since(start)): \(f.summary)")
            setStatus(device.id, .error(f.message))
            onNotice?("Release failed", "\(name): \(f.message)")
        } else {
            Log.engine.log("\(name): disconnected in \(Elapsed.since(start))\(timeline())")
            setStatus(device.id, .disconnected)
            if notify { onNotice?("Disconnected", name) }
        }
    }

    /// Used by the menu/Settings to force a re-evaluation (e.g. after editing devices).
    func reevaluate() {
        seed()
    }

    // MARK: - Helpers

    /// A classified failure: what kind (drives the retry policy) and the human message.
    private struct Failure {
        let kind: BTFailureKind
        let message: String

        init(_ kind: BTFailureKind, _ message: String) {
            self.kind = kind
            self.message = message
        }

        init(_ error: Error) {
            if let bt = error as? BTError {
                self.init(bt.kind, bt.description)
            } else {
                self.init(.unknown, String(describing: error))
            }
        }

        var summary: String { "[\(kind.rawValue)] \(message)" }
    }

    /// Run one timed Bluetooth step under the per-call timeout and log its success. Failures
    /// propagate to the caller, which logs them with the classification.
    private func step(
        _ name: String, _ tryNumber: Int?, _ label: String, _ op: @escaping @Sendable () async throws -> Void
    ) async throws {
        let start = ContinuousClock.now
        try await withTimeout(store.config.btCallTimeoutSecs, op)
        let prefix = tryNumber.map { "try \($0): " } ?? ""
        Log.engine.debug("\(name): \(prefix)\(label) OK (\(Elapsed.since(start)))")
    }

    /// Run a step whose failure must not abort the flow, but must be visible in the log.
    private func bestEffort(
        _ device: BTDevice, _ what: String, _ op: @escaping @Sendable () async throws -> Void
    ) async {
        do {
            try await withTimeout(store.config.btCallTimeoutSecs, op)
        } catch {
            Log.engine.warn("\(device.name): \(what) failed (continuing): \(Failure(error).summary)")
        }
    }

    private func markBusy(_ id: UUID) { busyCounts[id, default: 0] += 1 }

    private func clearBusy(_ id: UUID) {
        guard let n = busyCounts[id] else { return }
        busyCounts[id] = n <= 1 ? nil : n - 1
    }

    private func isBusy(_ id: UUID) -> Bool { busyCounts[id] != nil }

    /// An aborted connect resets only the status it set itself (connecting / a per-try error).
    /// If a newer run already reported connected/disconnected, that result stands.
    private func markAborted(_ id: UUID) {
        switch statuses[id] {
        case .connecting, .error: setStatus(id, .idle)
        default: break
        }
    }

    private func setStatus(_ id: UUID, _ status: DeviceStatus) {
        let old = statuses[id]
        guard old != status else { return }
        statuses[id] = status
        if status == .connected { becameConnected(id) }
    }

    // MARK: - Channel switching (spec §5a type 5)

    private let hostSwitcher: HostSwitcher?
    /// True once an evaluation acted on real presence, so `selected` can be trusted.
    private var presenceEvaluated = false
    /// Devices whose channel was marked this session.
    private var channelMarked = Set<UUID>()
    private var lastLateRelease: [UUID: Date] = [:]
    /// A device that keeps waking up here while the KVM is away is sent on at most this often.
    var lateReleaseMinInterval: TimeInterval = 10
    /// Devices seen answering the channel query this session (type 5).
    private var channelSwitchers = Set<UUID>()
    /// How long a channel-switching device is given to come over after the KVM arrives.
    var channelArrivalWaitMs = 20_000

    /// Whether the device is known to switch channels: it answered here this session, or the user
    /// picked the other computer's channel for it. Pairing-managed devices never are.
    func switchesChannels(_ device: BTDevice) -> Bool {
        guard hostSwitcher != nil, !device.managePairing else { return false }
        return channelSwitchers.contains(device.id) || device.releaseToChannel != nil
    }

    /// Settings learned it switches channels (it answered the query while connected).
    func noteSwitchesChannels(_ id: UUID) { channelSwitchers.insert(id) }

    /// Wait (without calling it) for a channel-switching device to arrive. The other computer's
    /// AutoSwitch sends it here when the KVM leaves that computer.
    private func waitForChannelArrival(
        _ device: BTDevice, run: Run, respectSelection: Bool, start: ContinuousClock.Instant
    ) async {
        let addr = device.normalizedAddress
        let polls = max(1, channelArrivalWaitMs / max(1, hidPollIntervalMs))
        Log.engine.debug(
            "\(device.name): switches channels; waiting for it to come over (up to \(channelArrivalWaitMs) ms)")
        for _ in 0...polls {
            if let why = staleReason(run, respectSelection: respectSelection) {
                Log.engine.debug("\(device.name): stopped waiting for it to come over: \(why)")
                markAborted(device.id)
                return
            }
            if await bt.isConnected(addr), await bt.isHIDReady(addr) != false {
                let timeline = run.since.map { "; \(Elapsed.since($0)) after the source appeared" } ?? ""
                Log.engine.log(
                    "\(device.name): connected ✓ (came over from the other computer) in \(Elapsed.since(start))\(timeline)"
                )
                setStatus(device.id, .connected)
                return
            }
            await sleepHook(hidPollIntervalMs)
        }
        Log.engine.log(
            "\(device.name): didn't come over within \(channelArrivalWaitMs / 1000) s. The other computer's AutoSwitch "
                + "sends it here; if that one isn't running, press the device's channel key")
        setStatus(device.id, .disconnected)
    }

    /// A device that switches channel on request is sent to the other computer's channel instead
    /// of being disconnected. True when it was sent; false when it doesn't support that, isn't
    /// connected here, or the target channel is unknown (the caller then releases it as usual).
    private func trySwitchChannel(_ device: BTDevice, why: String) async -> Bool {
        guard let hostSwitcher else { return false }
        let addr = device.normalizedAddress
        let channels: HostChannels?
        do {
            channels = try await withTimeout(store.config.btCallTimeoutSecs) { try await hostSwitcher.channels(addr) }
        } catch {
            Log.engine.debug("\(device.name): channel query failed: \(Failure(error).summary)")
            return false
        }
        guard let ch = channels else { return false }  // no channel switching, or not connected here
        channelSwitchers.insert(device.id)

        let target: Int?
        if let n = device.releaseToChannel, n >= 1, n <= ch.count {
            target = n - 1
        } else {
            target = ch.markedOtherChannel
        }
        guard let target else {
            Log.engine.warn(
                "\(device.name): it can switch channels, but the other computer's channel isn't known (set it in "
                    + "the device's settings, or run AutoSwitch on the other computer)")
            return false
        }
        guard target != ch.current else {
            Log.engine.warn(
                "\(device.name): the other computer's channel is set to \(target + 1), but that's this Mac's "
                    + "channel; check the setting")
            return false
        }
        do {
            try await withTimeout(store.config.btCallTimeoutSecs) {
                try await hostSwitcher.switchTo(addr, channel: target)
            }
        } catch {
            Log.engine.warn("\(device.name): switching to channel \(target + 1) failed: \(Failure(error).summary)")
            return false
        }
        Log.engine.log("\(device.name): sent to channel \(target + 1) (\(why))")
        return true
    }

    /// A device became connected. KVM here: mark our channel (once per session) so the other
    /// computer can find it. KVM elsewhere (the device was asleep at the switch and woke up on its
    /// old channel): send it on, rate-limited.
    private func becameConnected(_ id: UUID) {
        guard let hostSwitcher,
            let device = store.config.devices.first(where: { $0.id == id }),
            device.enabled, !device.managePairing
        else { return }
        if selected {
            guard channelMarked.insert(id).inserted else { return }
            let addr = device.normalizedAddress
            Task { @MainActor [weak self] in
                guard let self else { return }
                do {
                    let marked = try await withTimeout(self.store.config.btCallTimeoutSecs) {
                        try await hostSwitcher.markCurrentChannel(addr)
                    }
                    if let marked {
                        self.channelSwitchers.insert(id)
                        Log.engine.log(
                            "\(device.name): marked channel \(marked + 1) as this Mac's (for the other computer's AutoSwitch)"
                        )
                    } else if try await hostSwitcher.channels(addr) != nil {
                        self.channelSwitchers.insert(id)  // already marked: still a channel switcher
                    }
                } catch {
                    self.channelMarked.remove(id)
                    Log.engine.debug("\(device.name): could not mark this Mac's channel: \(Failure(error).summary)")
                }
            }
            return
        }
        guard presenceEvaluated, !store.config.paused, !suspended, !isBusy(id) else { return }
        let now = Date()
        if let last = lastLateRelease[id], now.timeIntervalSince(last) < lateReleaseMinInterval { return }
        lastLateRelease[id] = now
        Log.engine.log("\(device.name): connected while the KVM is on the other computer → sending it there")
        Task { @MainActor [weak self] in
            guard let self else { return }
            self.markBusy(id)
            defer { self.clearBusy(id) }
            if await self.trySwitchChannel(device, why: "it connected here while the KVM is elsewhere") {
                self.setStatus(id, .disconnected)
            }
        }
    }
}
