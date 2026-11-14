import Foundation
import IOBluetooth
import IOKit

/// Native IOBluetooth implementation.
///
/// connect/disconnect/isConnected/pair use public IOBluetooth APIs.
/// `unpair` uses the private `-[IOBluetoothDevice remove]` selector (the same call `blueutil`
/// relies on) because there is no public API to remove a bond. If it ever proves unreliable, swap
/// in a bundled blueutil behind this same protocol.
///
/// All calls hop to the main actor — IOBluetooth expects a live run loop on the main thread.
/// Connecting is asynchronous (it used to block the main thread for up to 20 s). The remaining
/// synchronous calls (`closeConnection`, `remove`) return at once; `blockingCall` logs a warning if
/// one ever holds the main thread for more than 2 s.
///
/// Logging: every raw call logs its outcome and duration here (category `bluetooth`); the engine
/// logs its decisions (category `engine`). Result codes are decoded by `BTCode`.
@MainActor
final class IOBluetoothController: NSObject, BluetoothController {

    /// Synchronous calls that hold the main thread longer than this are logged as warnings.
    private static let mainThreadBlockWarning: Duration = .seconds(2)

    // MARK: Monitoring state
    private var connectObserver: IOBluetoothUserNotification?
    private var disconnectObservers: [String: IOBluetoothUserNotification] = [:]
    private var monitoredAddresses = Set<String>()
    private var onChange: ((String, Bool) -> Void)?

    /// Pairing ceremonies in flight, by normalized address. `unpair` cancels a ceremony on the
    /// same device: a release must win over a pair that is still running, or the pair can land
    /// after the release and leave this Mac holding the bond while switched away.
    private var activePairings: [String: PairingHelper] = [:]

    /// Last reported adapter-availability problem, so it is logged once per change, not per poll.
    private var lastPowerProblem: String?

    private static func normalize(_ address: String) -> String {
        address.replacingOccurrences(of: ":", with: "-").lowercased()
    }

    private func device(_ address: String) throws -> IOBluetoothDevice {
        let normalized = Self.normalize(address)
        guard let dev = IOBluetoothDevice(addressString: normalized) else {
            Log.bluetooth.error("'\(address)' is not a valid Bluetooth address (IOBluetoothDevice returned nil)")
            throw BTError.invalidAddress(address)
        }
        return dev
    }

    /// Run a synchronous IOBluetooth call, timing it and flagging long main-thread stalls.
    private func blockingCall<T>(_ what: String, _ address: String, _ body: () -> T) -> (T, Duration) {
        let start = ContinuousClock.now
        let result = body()
        let took = start.duration(to: .now)
        if took > Self.mainThreadBlockWarning {
            Log.bluetooth.warn(
                "\(what) \(address) held the main thread for \(Elapsed.format(took)); "
                    + "USB events, timers and the UI were stalled meanwhile")
        }
        return (result, took)
    }

    func isConnected(_ address: String) async -> Bool {
        guard let dev = try? device(address) else { return false }
        return dev.isConnected() || Self.lowEnergyHIDRegistered(address)
    }

    // MARK: Bluetooth LE (HID over GATT)

    /// IOBluetooth is Classic-only: it doesn't list LE devices, can't open or close their links,
    /// and its connected flag stays false for them. macOS connects a bonded LE keyboard by itself
    /// when it advertises, and bluetoothd then publishes its HID services as registered
    /// `IOHIDDevice`s with `Transport = "Bluetooth Low Energy"` and `DeviceAddress =
    /// "e7-99-a2-72-a6-76"` (no `SerialNumber`; seen on a ZMK Reviung41, 2026-11-14). That is the
    /// LE device's "connected and HID up" signal here.
    static func lowEnergyHIDRegistered(_ address: String) -> Bool {
        let want = normalize(address)
        let matching = IOServiceMatching("IOHIDDevice") as NSMutableDictionary
        matching[kIOPropertyMatchKey] = ["Transport": "Bluetooth Low Energy"]
        var iterator: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iterator) == KERN_SUCCESS else {
            return false
        }
        defer { IOObjectRelease(iterator) }
        while case let service = IOIteratorNext(iterator), service != 0 {
            defer { IOObjectRelease(service) }
            let property = IORegistryEntryCreateCFProperty(service, "DeviceAddress" as CFString, kCFAllocatorDefault, 0)
            if let found = property?.takeRetainedValue() as? String, normalize(found) == want { return true }
        }
        return false
    }

    /// An LE device as far as IOBluetooth can tell: connected over LE right now, or no Classic
    /// bond and no class of device (IOBluetooth has never seen it over Classic).
    private func isLowEnergy(_ dev: IOBluetoothDevice, _ address: String) -> Bool {
        Self.lowEnergyHIDRegistered(address) || (!dev.isPaired() && dev.classOfDevice == 0)
    }

    /// Addresses whose last HID check found nothing (so the diagnostic is logged once per miss).
    private var hidMissLogged = Set<String>()

    /// HID is up when bluetoothd has published a *registered* IOHIDDevice for the device:
    /// `Transport = "Bluetooth"`, `SerialNumber = "3C:50:02:BF:22:45"` (seen on hardware; bluetoothd
    /// creates it as an `IOHIDUserDevice` when the HID channels open). Registry matching only
    /// returns registered services. That matters: after failed attempts, macOS leaves
    /// unregistered `IOHIDUserDevice` leftovers with the same serial, which a raw registry walk
    /// would mistake for a live device.
    func isHIDReady(_ address: String) async -> Bool? {
        if Self.lowEnergyHIDRegistered(address) { return true }
        // Only peripherals (keyboards, mice, trackpads, controllers) bring up HID. For anything
        // else (headphones: spec §5a type 4) we can't tell, so the engine trusts the link, as
        // on Windows. Without this a connected headset counted as "link up but no HID".
        if let dev = IOBluetoothDevice(addressString: Self.normalize(address)), dev.classOfDevice != 0,
            dev.deviceClassMajor != kBluetoothDeviceClassMajorPeripheral
        {
            return nil
        }
        let want = Self.normalize(address)
        let matching = IOServiceMatching("IOHIDDevice") as NSMutableDictionary
        matching[kIOPropertyMatchKey] = ["Transport": "Bluetooth"]
        var iterator: io_iterator_t = 0
        let kr = IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iterator)
        guard kr == KERN_SUCCESS else {
            Log.bluetooth.warn(
                "HID check for \(address): registry query failed (kern_return \(kr)); not verifying HID")
            return nil
        }
        defer { IOObjectRelease(iterator) }
        var serials: [String] = []
        while case let service = IOIteratorNext(iterator), service != 0 {
            defer { IOObjectRelease(service) }
            let property = IORegistryEntryCreateCFProperty(
                service, "SerialNumber" as CFString, kCFAllocatorDefault, 0)
            guard let serial = property?.takeRetainedValue() as? String else { continue }
            if Self.normalize(serial) == want {
                hidMissLogged.remove(want)
                return true
            }
            serials.append(serial)
        }
        if !hidMissLogged.contains(want) {
            hidMissLogged.insert(want)
            let found = serials.isEmpty ? "none" : serials.joined(separator: ", ")
            Log.bluetooth.debug(
                "HID check for \(address): no registered HID device yet (registered Bluetooth HID serials: \(found))")
        }
        return false
    }

    /// Async connection attempts started by `connect`, by normalized address: `.pending` until
    /// `connectionComplete:status:` reports, then `.done(status)`.
    private enum ConnectState { case pending, done(IOReturn) }
    private var connectAttempts: [String: ConnectState] = [:]
    /// How long `connect` waits for the link before failing. It never blocks the main thread.
    private static let connectWait: Duration = .seconds(12)

    func connect(_ address: String) async throws {
        let key = Self.normalize(address)
        let dev = try device(address)
        // Asynchronous open, then wait cooperatively. Until 2026-11-12 this used the synchronous
        // openConnection(), whose result was the success signal (the async callback had proved
        // unreliable on its own). On hardware that call held the main thread for 20 s on every
        // failed try, ten times in one session. HID had already failed after 1.5 s, but USB
        // switch-aways and the UI stalled meanwhile, delaying this Mac's release for the other host.
        // Success is now judged by the engine's HID check (`isHIDReady`). Here the callback is only
        // an early hint, and the link state is the fallback when the callback never fires.
        if isLowEnergy(dev, address) {
            try await waitForLowEnergy(address)
            return
        }
        let wasConnected = dev.isConnected()
        let note = wasConnected ? " (already connected; reopening for a clean connect)" : ""
        Log.bluetooth.debug("openConnection \(address) (async)…\(note)")
        let start = ContinuousClock.now
        connectAttempts[key] = .pending
        let rc = dev.openConnection(self)
        guard rc == kIOReturnSuccess else {
            connectAttempts[key] = nil
            Log.bluetooth.debug("openConnection \(address) could not start: \(BTCode.describe(rc))")
            throw BTError.connectFailed(rc)
        }
        defer { if case .done = connectAttempts[key] { connectAttempts[key] = nil } }
        while true {
            if case .done(let status) = connectAttempts[key] {
                guard status == kIOReturnSuccess else {
                    Log.bluetooth.debug(
                        "openConnection \(address) failed after \(Elapsed.since(start)): \(BTCode.describe(status))")
                    throw BTError.connectFailed(status)
                }
                Log.bluetooth.debug("openConnection \(address) OK (\(Elapsed.since(start)))")
                return
            }
            if !wasConnected, dev.isConnected() {
                Log.bluetooth.debug(
                    "openConnection \(address): link up after \(Elapsed.since(start)) (callback pending)")
                return
            }
            if start.duration(to: .now) > Self.connectWait {
                Log.bluetooth.debug("openConnection \(address): no link after \(Elapsed.since(start)); giving up")
                throw BTError.connectFailed(Int32(bitPattern: 0xE000_02D6))  // kIOReturnTimeout
            }
            try await Task.sleep(nanoseconds: 100_000_000)  // throws when withTimeout cancels us
        }
    }

    /// "Connecting" an LE device: macOS can't call it, so wait for it to connect by itself (it does
    /// as soon as it advertises to this Mac, e.g. after its profile was switched here).
    private func waitForLowEnergy(_ address: String) async throws {
        let start = ContinuousClock.now
        Log.bluetooth.debug("connect \(address): Bluetooth LE device; waiting for it to connect by itself")
        while !Self.lowEnergyHIDRegistered(address) {
            if start.duration(to: .now) > Self.connectWait {
                Log.bluetooth.debug("connect \(address): the LE device didn't connect within \(Elapsed.since(start))")
                throw BTError.classified(
                    .timeout,
                    "the Bluetooth LE device didn't connect (macOS can't call an LE device; it connects by itself "
                        + "when it's on this Mac's profile)")
            }
            try await Task.sleep(nanoseconds: 100_000_000)  // throws when withTimeout cancels us
        }
        Log.bluetooth.debug("connect \(address): LE device connected (\(Elapsed.since(start)))")
    }

    /// Completion of an async `openConnection(_:)` (IOBluetooth's informal target protocol).
    @objc(connectionComplete:status:)
    func connectionComplete(_ device: IOBluetoothDevice?, status: IOReturn) {
        guard let raw = device?.addressString else { return }
        let key = Self.normalize(raw)
        guard connectAttempts[key] != nil else { return }
        connectAttempts[key] = .done(status)
    }

    func disconnect(_ address: String) async throws {
        let key = Self.normalize(address)
        let dev = try device(address)
        // A pending async open must be cancelled too, or the link can come up after a release.
        var pending = false
        if case .pending = connectAttempts[key] { pending = true }
        guard dev.isConnected() || pending else {
            if Self.lowEnergyHIDRegistered(address) {
                throw BTError.classified(
                    .unknown,
                    "macOS has no call to disconnect a Bluetooth LE device; it stays connected until it switches "
                        + "profile (its profile key)")
            }
            Log.bluetooth.debug("closeConnection \(address): not connected, nothing to close")
            return
        }
        connectAttempts[key] = nil
        let (rc, took) = blockingCall("closeConnection", address) { dev.closeConnection() }
        guard rc == kIOReturnSuccess || (pending && !dev.isConnected()) else {
            Log.bluetooth.debug(
                "closeConnection \(address) failed after \(Elapsed.format(took)): \(BTCode.describe(rc))")
            throw BTError.disconnectFailed(rc)
        }
        let note = pending ? " (cancelled a pending connect)" : ""
        Log.bluetooth.debug("closeConnection \(address) OK (\(Elapsed.format(took)))\(note)")
    }

    func pair(_ address: String) async throws {
        let key = Self.normalize(address)
        let dev = try device(address)
        if dev.isPaired() {
            // Logged at INFO on purpose: this skip is the first clue when a stale bond causes
            // repeated connect timeouts (the engine then re-pairs from scratch on its next try).
            Log.bluetooth.debug("pair \(address): a bond already exists; skipping the pairing ceremony")
            return
        }
        if activePairings[key] != nil {
            Log.bluetooth.debug("pair \(address): a pairing ceremony with this device is already running")
            throw BTError.classified(.operationInProgress, "a pairing with this device is already running")
        }
        let helper = PairingHelper(address: address)
        activePairings[key] = helper
        defer { if activePairings[key] === helper { activePairings[key] = nil } }

        Log.bluetooth.debug("pair \(address): starting pairing ceremony")
        let start = ContinuousClock.now
        do {
            try await helper.pair(dev)
            Log.bluetooth.debug("pair \(address): paired (\(Elapsed.since(start)))")
        } catch {
            Log.bluetooth.debug("pair \(address) failed after \(Elapsed.since(start)): \(error)")
            throw error
        }
    }

    /// Private-API unpair via `-[IOBluetoothDevice remove]`.
    func unpair(_ address: String) async throws {
        let key = Self.normalize(address)
        let dev = try device(address)

        if let helper = activePairings[key] {
            Log.bluetooth.warn("unpair \(address): cancelling a pairing ceremony still in progress (the release wins)")
            helper.cancel("superseded by unpair")
        }

        guard dev.isPaired() else {
            Log.bluetooth.debug("unpair \(address): not paired, nothing to remove")
            return
        }

        let sel = NSSelectorFromString("remove")
        guard dev.responds(to: sel),
            let method = class_getInstanceMethod(type(of: dev), sel)
        else {
            Log.bluetooth.error("unpair \(address): -[IOBluetoothDevice remove] is unavailable on this macOS")
            throw BTError.unpairUnavailable
        }
        typealias RemoveIMP = @convention(c) (AnyObject, Selector) -> Int32
        let imp = method_getImplementation(method)
        let fn = unsafeBitCast(imp, to: RemoveIMP.self)
        // The return value is unreliable (the selector effectively returns void), so success is
        // judged by the pairing state afterwards.
        let (_, took) = blockingCall("remove", address) { fn(dev, sel) }

        if dev.isPaired() {
            Log.bluetooth.error("unpair \(address): the bond is still there after remove (\(Elapsed.format(took)))")
            throw BTError.unpairFailed("still paired after -remove")
        }
        // `-remove` returns at once, but bluetoothd *defers* the unpair: it drops the link
        // ~1 s later and deletes the link key ~1.5 s after that (seen on hardware 2026-11-12).
        // Wait for the link to go down, so the release is real when this returns and a following
        // pair doesn't collide with the unpair still in progress (that failed with HCI 0x02).
        let start = ContinuousClock.now
        var waits = 0
        while dev.isConnected(), waits < 40 {  // ≤ 4 s
            try? await Task.sleep(nanoseconds: 100_000_000)
            waits += 1
        }
        if dev.isConnected() {
            Log.bluetooth.warn("unpair \(address): bond removed but the link is still up after \(Elapsed.since(start))")
        } else {
            Log.bluetooth.debug(
                "unpair \(address) OK (remove \(Elapsed.format(took)), link down after \(Elapsed.since(start)); "
                    + "the system finishes deleting the key ~1.5 s later)")
        }
    }

    /// Classic bonds from IOBluetooth, plus the LE devices only `system_profiler` lists.
    func pairedDevices() async -> [PairedDeviceInfo] {
        guard let devices = IOBluetoothDevice.pairedDevices() as? [IOBluetoothDevice] else {
            Log.bluetooth.warn("pairedDevices: IOBluetooth returned no list (Bluetooth off or permission denied?)")
            return []
        }
        var list = devices.map {
            PairedDeviceInfo(
                name: $0.name ?? $0.addressString ?? "Unknown",
                address: $0.addressString ?? "")
        }
        let classic = Set(list.map { DeviceIdentityReader.normalize($0.address) })
        for other in await DeviceIdentityReader.systemPairedDevices()
        where !classic.contains(DeviceIdentityReader.normalize(other.address)) {
            list.append(other)
        }
        return list
    }

    func isPoweredOn() async -> Bool? {
        guard let host = IOBluetoothHostController.default() else {
            reportPowerProblem(
                "no Bluetooth host controller (adapter missing, or Bluetooth permission denied for this app)")
            return nil
        }
        reportPowerProblem(nil)
        return host.powerState == kBluetoothHCIPowerStateON
    }

    private func reportPowerProblem(_ problem: String?) {
        guard problem != lastPowerProblem else { return }
        lastPowerProblem = problem
        if let problem {
            Log.bluetooth.warn("adapter state unknown: \(problem)")
        } else {
            Log.bluetooth.log("Bluetooth host controller available")
        }
    }

    // MARK: - Monitoring (event-driven status)

    func startMonitoring(addresses: [String], onChange: @escaping (String, Bool) -> Void) {
        stopMonitoring()
        self.onChange = onChange
        self.monitoredAddresses = Set(addresses.map(Self.normalize))

        // One global observer fires whenever ANY device connects.
        connectObserver = IOBluetoothDevice.register(
            forConnectNotifications: self,
            selector: #selector(deviceConnected(_:device:)))
        if connectObserver == nil {
            Log.bluetooth.warn(
                "could not register for Bluetooth connect notifications; status relies on the 10 s poll")
        }

        // Report current state for each monitored address, and arm a disconnect watch on any
        // that are already connected.
        for addr in monitoredAddresses {
            guard let dev = IOBluetoothDevice(addressString: addr) else {
                Log.bluetooth.warn("monitoring: '\(addr)' is not a valid Bluetooth address; skipped")
                continue
            }
            let connected = dev.isConnected() || Self.lowEnergyHIDRegistered(addr)
            onChange(addr, connected)
            if connected { armDisconnect(dev, addr) }
        }
    }

    func stopMonitoring() {
        connectObserver?.unregister()
        connectObserver = nil
        disconnectObservers.values.forEach { $0.unregister() }
        disconnectObservers.removeAll()
        monitoredAddresses.removeAll()
        onChange = nil
    }

    private func armDisconnect(_ device: IOBluetoothDevice, _ addr: String) {
        disconnectObservers[addr]?.unregister()
        disconnectObservers[addr] = nil
        if let note = device.register(
            forDisconnectNotification: self,
            selector: #selector(deviceDisconnected(_:device:)))
        {
            disconnectObservers[addr] = note
        } else {
            Log.bluetooth.warn("could not arm the disconnect notification for \(addr); the 10 s poll covers it")
        }
    }

    @objc private func deviceConnected(_ note: IOBluetoothUserNotification, device: IOBluetoothDevice) {
        guard let addr = device.addressString?.lowercased(),
            monitoredAddresses.contains(addr)
        else { return }
        Log.bluetooth.debug("event: \(addr) connected")
        onChange?(addr, true)
        armDisconnect(device, addr)  // disconnect notifications are one-shot; re-arm each time
    }

    @objc private func deviceDisconnected(_ note: IOBluetoothUserNotification, device: IOBluetoothDevice) {
        guard let addr = device.addressString?.lowercased() else { return }
        disconnectObservers[addr]?.unregister()
        disconnectObservers[addr] = nil
        guard monitoredAddresses.contains(addr) else { return }
        Log.bluetooth.debug("event: \(addr) disconnected")
        onChange?(addr, false)
    }
}

/// Drives an `IOBluetoothDevicePair` to completion. Auto-confirms secure-simple-pairing
/// (numeric-comparison) prompts — fine for HID devices like a trackpad/keyboard. Handles the full
/// delegate surface so failures report the step that broke (with the stack's status code);
/// devices that demand a typed PIN can't be paired headlessly and fail with a clear message.
@MainActor
private final class PairingHelper: NSObject, @preconcurrency IOBluetoothDevicePairDelegate {
    private let address: String
    private var continuation: CheckedContinuation<Void, Error>?
    private var pairer: IOBluetoothDevicePair?
    /// HCI status from `deviceSimplePairingComplete`, used to explain a generic completion error.
    private var simplePairingStatus: BluetoothHCIEventStatus = 0

    init(address: String) {
        self.address = address
    }

    func pair(_ device: IOBluetoothDevice) async throws {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                self.continuation = cont
                guard let pairer = IOBluetoothDevicePair(device: device) else {
                    self.finish(.failure(BTError.pairFailed("could not create the pairing object")))
                    return
                }
                self.pairer = pairer
                pairer.delegate = self
                let rc = pairer.start()
                if rc != kIOReturnSuccess {
                    self.finish(.failure(BTError.pairFailed("start", code: rc)))
                }
            }
        } onCancel: {
            // withTimeout cancels the abandoned operation on deadline: stop the ceremony so it
            // can't complete in the background after the engine moved on.
            Task { @MainActor in self.finish(.failure(BTError.pairCancelled("timed out"))) }
        }
    }

    /// Finished pairing objects, held past the connect step (see `finish`).
    private static var lingering: [IOBluetoothDevicePair] = []
    private static func linger(_ pairer: IOBluetoothDevicePair) {
        lingering.append(pairer)
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 30_000_000_000)
            lingering.removeAll { $0 === pairer }
        }
    }

    /// Stop the ceremony (e.g. a release for the same device superseded it).
    func cancel(_ reason: String) {
        finish(.failure(BTError.pairCancelled(reason)))
    }

    private func finish(_ result: Result<Void, Error>) {
        if case .success = result, let done = pairer {
            // No stop() after a successful ceremony: stop() closes the link the pairing object
            // opened, and on hardware (2026-11-12) that cut bluetoothd's own HID setup on the
            // pairing link (result 2603). bluetoothd then re-paged the trackpad within ~150-450 ms,
            // the trackpad opened HID on that link before the new key was usable, the Mac refused
            // it (L2CAP policy STATUS 706), and every later HID attempt failed 706, even after a
            // fresh re-pair, until the trackpad bonded elsewhere or was power-cycled.
            // The finished object is kept alive a while so releasing it can't close the link either.
            done.delegate = nil
            Self.linger(done)
        } else {
            pairer?.stop()
        }
        pairer = nil
        guard let cont = continuation else { return }
        continuation = nil
        switch result {
        case .success: cont.resume()
        case .failure(let e): cont.resume(throwing: e)
        }
    }

    // MARK: IOBluetoothDevicePairDelegate

    func devicePairingStarted(_ sender: Any!) {
        Log.bluetooth.debug("pair \(address): ceremony started")
    }

    func devicePairingConnecting(_ sender: Any!) {
        Log.bluetooth.debug("pair \(address): connecting")
    }

    /// Secure-simple-pairing numeric comparison — auto-accept (no UI for a background agent).
    func devicePairingUserConfirmationRequest(_ sender: Any!, numericValue: BluetoothNumericValue) {
        Log.bluetooth.debug("pair \(address): confirming numeric comparison automatically")
        (sender as? IOBluetoothDevicePair)?.replyUserConfirmation(true)
    }

    /// Passkey the *device* displays; informational, no reply needed.
    func devicePairingUserPasskeyNotification(_ sender: Any!, passkey: BluetoothPasskey) {
        Log.bluetooth.debug("pair \(address): device shows passkey \(passkey)")
    }

    /// Legacy typed-PIN request. Modern SSP HID devices (trackpad/keyboard) shouldn't reach here;
    /// log it and let the attempt run its course rather than aborting a potentially-valid pairing.
    func devicePairingPINCodeRequest(_ sender: Any!) {
        Log.bluetooth.warn("pair \(address): the device asked for a PIN code (none supplied; pairing will likely fail)")
    }

    func deviceSimplePairingComplete(_ sender: Any!, status: BluetoothHCIEventStatus) {
        simplePairingStatus = status
        if status != 0 {
            Log.bluetooth.debug("pair \(address): simple pairing completed with \(BTCode.describe(Int32(status)))")
        }
    }

    func devicePairingFinished(_ sender: Any!, error: IOReturn) {
        if error == kIOReturnSuccess {
            finish(.success(()))
            return
        }
        // A generic completion error is less useful than the HCI status of the simple-pairing
        // step (e.g. authentication failure), when that step reported one.
        let generic = Int32(bitPattern: 0xE000_02BC)  // kIOReturnError
        let code = (error == generic && simplePairingStatus != 0) ? Int32(simplePairingStatus) : error
        finish(.failure(BTError.pairFailed("completion", code: code)))
    }
}
