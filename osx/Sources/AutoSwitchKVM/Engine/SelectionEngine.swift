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
/// Bluetooth devices. Mirrors the Hammerspoon prototype (debounce, bounded retries,
/// abort-if-deselected, pair/unpair where configured).
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
    private var presentProductIDs = Set<UInt16>()
    /// Presence the automation last connected/disconnected for (latch, distinct from `selected`
    /// which tracks raw presence even while paused).
    private var automationActed = false
    private var debounceTimer: Timer?
    private var runToken = 0  // bumps on every evaluation to cancel in-flight work

    private var statusTimer: Timer?
    /// Devices with an in-flight connect/disconnect; the poller leaves their status alone.
    private var busyDevices = Set<UUID>()

    // Auto-reconnect scheduling (internal so tests can make it deterministic).
    var autoReconnectDelayMs = 2000
    var autoReconnectMinInterval: TimeInterval = 30
    private var lastAutoReconnect: [UUID: Date] = [:]

    init(store: ConfigStore, usb: USBMonitoring, bt: BluetoothController) {
        self.store = store
        self.usb = usb
        self.bt = bt

        // Events are delivered on the main queue (USBMonitor dispatches there), so assumeIsolated
        // is safe and lets us call the @MainActor handler from the Combine closure.
        usb.events
            .sink { [weak self] event in MainActor.assumeIsolated { self?.handleUSB(event) } }
            .store(in: &cancellables)

        // Re-register Bluetooth observers whenever the device list changes (debounced so rapid
        // settings edits don't thrash). dropFirst avoids double-registering at startup.
        // DispatchQueue.main, not RunLoop.main: the run-loop scheduler stalls in tracking mode
        // (menu open / modal panel), which would freeze automation while the UI is interacted with.
        store.$config
            .map(\.devices)
            .removeDuplicates()
            .debounce(for: .milliseconds(300), scheduler: DispatchQueue.main)
            .dropFirst()
            .sink { [weak self] _ in MainActor.assumeIsolated { self?.refreshMonitoring() } }
            .store(in: &cancellables)
    }

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
        guard !busyDevices.contains(device.id) else { return }
        updateStatus(device, connected: connected)
    }

    private func pollStatuses() async {
        await refreshPower()
        for device in store.config.devices {
            guard !busyDevices.contains(device.id) else { continue }
            if !bluetoothPowered {
                if statuses[device.id] != .bluetoothOff { statuses[device.id] = .bluetoothOff }
                continue
            }
            let connected = await bt.isConnected(device.normalizedAddress)
            updateStatus(device, connected: connected)
        }
    }

    /// Reflect a device's live connection state, and flag a passive drop (connected → disconnected
    /// while the source is present, BT is on, and we're not mid-transition) as unexpected.
    private func updateStatus(_ device: BTDevice, connected: Bool) {
        let old = statuses[device.id]
        let new: DeviceStatus = connected ? .connected : .disconnected
        if old != new { statuses[device.id] = new }

        if !connected, old == .connected, selected, bluetoothPowered, device.enabled {
            Log.engine.log("\(device.name) dropped unexpectedly while source present")
            onUnexpectedDisconnect?(device)
            maybeAutoReconnect(device)
        }
    }

    /// One bounded, rate-limited reconnect after an unexpected drop (opt-out via
    /// `autoReconnectOnDrop`). Uses the current runToken so any newer run supersedes it;
    /// combined with the retry escalation (re-pair from scratch on attempt ≥ 2) this
    /// self-heals even when the device stopped honoring its bond.
    private func maybeAutoReconnect(_ device: BTDevice) {
        guard store.config.autoReconnectOnDrop, !store.config.paused else { return }
        guard !busyDevices.contains(device.id) else { return }
        let now = Date()
        if let last = lastAutoReconnect[device.id], now.timeIntervalSince(last) < autoReconnectMinInterval {
            Log.engine.log("not auto-reconnecting \(device.name) (rate-limited)")
            return
        }
        lastAutoReconnect[device.id] = now
        Log.engine.log("auto-reconnecting \(device.name) after unexpected drop")

        let token = runToken
        let delayMs = autoReconnectDelayMs
        Task { @MainActor [weak self] in
            if delayMs > 0 { try? await Task.sleep(nanoseconds: UInt64(delayMs) * 1_000_000) }
            guard let self, token == self.runToken, self.selected, self.bluetoothPowered,
                !self.store.config.paused, !self.busyDevices.contains(device.id)
            else { return }
            self.busyDevices.insert(device.id)
            defer { self.busyDevices.remove(device.id) }
            await self.connectOne(device, token: token, respectSelection: true)
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
            Log.engine.log("Bluetooth powered off")
        }
    }

    /// Called at launch and on wake to establish current presence from scratch.
    func seed() {
        let source = store.config.source
        presentProductIDs.removeAll()
        automationActed = false  // re-evaluate from scratch (covers profile switches and wake)
        if let source {
            for dev in usb.attachedDevices
            where dev.vendorID == source.vendorID && source.productIDs.contains(dev.productID) {
                presentProductIDs.insert(dev.productID)
            }
        }
        lastReason = "initial scan"
        scheduleEvaluate()
    }

    // MARK: - USB handling

    /// Internal (not private) so unit tests can simulate USB events directly.
    func handleUSB(_ event: (vendorID: UInt16, productID: UInt16, added: Bool)) {
        guard let source = store.config.source,
            event.vendorID == source.vendorID,
            source.productIDs.contains(event.productID)
        else { return }

        if event.added {
            presentProductIDs.insert(event.productID)
            lastReason = String(format: "source arrived (0x%04X)", event.productID)
        } else {
            presentProductIDs.remove(event.productID)
            lastReason = String(format: "source removed (0x%04X)", event.productID)
        }
        scheduleEvaluate()
    }

    private func scheduleEvaluate() {
        debounceTimer?.invalidate()
        // Source present now → arrival debounce (snappier connect); gone → departure debounce.
        let ms = presentProductIDs.isEmpty ? store.config.debounceMs : store.config.arrivalDebounceMs
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
        let present = !presentProductIDs.isEmpty
        selected = present  // always reflect real presence for the UI

        guard !store.config.paused else { return }  // automation suspended; don't act

        // `automationActed` latches the presence the automation last acted on, so resuming from
        // pause reconciles to the current state.
        if present && !automationActed {
            automationActed = true
            runToken += 1
            Log.engine.log("source present → connecting enabled devices")
            await connectAll(token: runToken)
        } else if !present && automationActed {
            automationActed = false
            runToken += 1
            Log.engine.log("source absent → disconnecting enabled devices")
            await disconnectAll(token: runToken)
        }
    }

    // MARK: - Manual quick actions (menu) — operate on all configured devices, ignore selection

    func connectAllNow() { Task { @MainActor in await connectAllNowImpl() } }
    func disconnectAllNow() { Task { @MainActor in await disconnectAllNowImpl() } }

    func connectAllNowImpl() async {
        runToken += 1
        let token = runToken
        for device in store.config.devices {
            guard token == runToken else { return }  // superseded by a newer run
            busyDevices.insert(device.id)
            defer { busyDevices.remove(device.id) }
            await connectOne(device, token: token, respectSelection: false)
        }
    }

    func disconnectAllNowImpl() async {
        runToken += 1  // invalidate any in-flight connect run — the user asked for release
        let token = runToken
        for device in store.config.devices {
            guard token == runToken else { return }
            busyDevices.insert(device.id)
            defer { busyDevices.remove(device.id) }
            await disconnectOne(device, token: token)
        }
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

    // MARK: - Actions

    private func connectAll(token: Int) async {
        for device in store.config.devices where device.enabled {
            guard token == runToken else { return }
            busyDevices.insert(device.id)
            defer { busyDevices.remove(device.id) }
            await connectOne(device, token: token, respectSelection: true)
        }
    }

    private func connectOne(_ device: BTDevice, token: Int, respectSelection: Bool) async {
        let cfg = store.config
        let addr = device.normalizedAddress
        statuses[device.id] = .connecting

        // Optional per-device stagger before connecting.
        if device.connectDelayMs > 0 {
            try? await Task.sleep(nanoseconds: UInt64(device.connectDelayMs) * 1_000_000)
            guard token == runToken, (!respectSelection || selected) else {
                statuses[device.id] = .idle; return
            }
        }

        var attempt = 0
        while attempt < cfg.connectRetryMax {
            if !bluetoothPowered { statuses[device.id] = .bluetoothOff; return }
            let aborted = token != runToken || (respectSelection && !selected)
            guard !aborted else { statuses[device.id] = .idle; return }
            attempt += 1
            Log.engine.log(
                "connect \(device.name): attempt \(attempt)/\(cfg.connectRetryMax) (managePairing=\(device.managePairing))"
            )

            if !device.managePairing, await bt.isConnected(addr) {
                Log.bluetooth.log("\(device.name) already connected")
                statuses[device.id] = .connected
                return
            }

            // A clean connect = `connect` returned without throwing, i.e. IOBluetooth's
            // `connectionComplete` fired with success. That's the reliable "HID is up" signal;
            // `isConnected()` alone can be a link-only (ACL) state that doesn't actually work.
            var connectedCleanly = false
            do {
                if device.managePairing {
                    if attempt > 1 {
                        // A bond that survived a failed attempt may be a link key the device no
                        // longer honors (the other host re-paired it, or it was power-cycled) —
                        // observed on hardware as endless "pair: skipped" + openConnection
                        // timeouts. Re-pair from scratch; pair() alone short-circuits on
                        // isPaired(), so drop the stale bond first (best-effort).
                        Log.bluetooth.log("re-pairing \(device.name) from scratch (attempt \(attempt))")
                        try? await withTimeout(cfg.btCallTimeoutSecs) { try await self.bt.unpair(addr) }
                    }
                    Log.bluetooth.log("pair \(device.name)…")
                    try await withTimeout(cfg.btCallTimeoutSecs) { try await self.bt.pair(addr) }
                    Log.bluetooth.log("pair \(device.name): ok")
                    // The connection IOBluetooth reports right after pairing is unreliable; drop it
                    // and reconnect cleanly.
                    try? await withTimeout(cfg.btCallTimeoutSecs) { try await self.bt.disconnect(addr) }
                    try? await Task.sleep(nanoseconds: 800_000_000)
                    Log.bluetooth.log("post-pair settle done; reconnecting \(device.name)")
                }
                Log.bluetooth.log("openConnection \(device.name)…")
                try await withTimeout(cfg.btCallTimeoutSecs) { try await self.bt.connect(addr) }
                connectedCleanly = true
                Log.bluetooth.log("openConnection \(device.name): completed cleanly")
            } catch {
                if case BTError.timeout = error, await bt.isConnected(addr) {
                    // openConnection can complete just after the deadline (withTimeout abandons
                    // it rather than waiting). A connection that arrived late is a success —
                    // don't tear it down and burn a retry.
                    connectedCleanly = true
                    Log.bluetooth.log("connect \(device.name): completed just after timeout; keeping it")
                } else {
                    statuses[device.id] = .error(String(describing: error))
                    Log.bluetooth.error("connect \(device.name): \(error)")
                }
            }

            if connectedCleanly, await bt.isConnected(addr) {
                Log.bluetooth.log("\(device.name) connected ✓")
                statuses[device.id] = .connected
                onNotice?("Connected", device.name)
                return
            } else {
                // Don't trust a link-only state; drop any partial connection so the next attempt is clean.
                Log.bluetooth.log(
                    "\(device.name) not cleanly connected after attempt \(attempt); dropping partial link")
                try? await withTimeout(cfg.btCallTimeoutSecs) { try await self.bt.disconnect(addr) }
            }

            if attempt < cfg.connectRetryMax {
                try? await Task.sleep(nanoseconds: Self.backoffNanos(base: cfg.connectRetrySecs, attempt: attempt))
            }
        }
        if case .connected = statuses[device.id] ?? .idle {
        } else {
            statuses[device.id] = .error("gave up after \(cfg.connectRetryMax) attempts")
            onNotice?("Connection failed", "\(device.name): gave up after \(cfg.connectRetryMax) attempts")
        }
    }

    private func disconnectAll(token: Int) async {
        for device in store.config.devices where device.enabled {
            guard token == runToken else { return }  // superseded (e.g. the source came back)
            busyDevices.insert(device.id)
            defer { busyDevices.remove(device.id) }
            await disconnectOne(device, token: token)
        }
    }

    /// Tear down one device: disconnect, then (managePairing) unpair. `token` staleness is
    /// re-checked after every await — a newer run owns the devices once it starts, and a stale
    /// disconnect must not undo the connect/pair the new run just performed.
    private func disconnectOne(_ device: BTDevice, token: Int) async {
        let cfg = store.config
        let addr = device.normalizedAddress

        var disconnectError: Error?
        do {
            try await withTimeout(cfg.btCallTimeoutSecs) { try await self.bt.disconnect(addr) }
        } catch {
            disconnectError = error
            Log.bluetooth.error("disconnect \(device.name): \(error)")
        }
        guard token == runToken else { return }

        // Release the bond even if the disconnect call failed — the bond is what blocks the
        // other host (Magic Trackpad), and unpair tears the link down anyway.
        if device.managePairing {
            do {
                try await withTimeout(cfg.btCallTimeoutSecs) { try await self.bt.unpair(addr) }
            } catch {
                guard token == runToken else { return }
                statuses[device.id] = .error(String(describing: error))
                Log.bluetooth.error("unpair \(device.name): \(error)")
                return
            }
            guard token == runToken else { return }
            statuses[device.id] = .disconnected
            onNotice?("Disconnected", device.name)
            return
        }

        if let disconnectError {
            statuses[device.id] = .error(String(describing: disconnectError))
        } else {
            statuses[device.id] = .disconnected
            onNotice?("Disconnected", device.name)
        }
    }

    /// Used by the menu/Settings to force a re-evaluation (e.g. after editing devices).
    func reevaluate() {
        seed()
    }
}
