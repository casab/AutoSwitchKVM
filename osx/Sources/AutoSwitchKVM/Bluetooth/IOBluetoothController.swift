import Foundation
import IOBluetooth
import IOKit

/// Native IOBluetooth implementation.
///
/// connect/disconnect/isConnected/pair use public IOBluetooth APIs.
/// `unpair` uses the private `-[IOBluetoothDevice remove]` selector (the same call `blueutil`
/// relies on) because there is no public API to remove a bond. This is the Phase 0 spike: if it
/// proves unreliable, swap in a bundled blueutil behind this same protocol.
///
/// All calls hop to the main actor — IOBluetooth expects a live run loop on the main thread.
@MainActor
final class IOBluetoothController: NSObject, BluetoothController {

    // MARK: Monitoring state
    private var connectObserver: IOBluetoothUserNotification?
    private var disconnectObservers: [String: IOBluetoothUserNotification] = [:]
    private var monitoredAddresses = Set<String>()
    private var onChange: ((String, Bool) -> Void)?

    private func device(_ address: String) throws -> IOBluetoothDevice {
        let normalized = address.replacingOccurrences(of: ":", with: "-").lowercased()
        guard let dev = IOBluetoothDevice(addressString: normalized) else {
            throw BTError.invalidAddress(address)
        }
        return dev
    }

    func isConnected(_ address: String) async -> Bool {
        guard let dev = try? device(address) else { return false }
        return dev.isConnected()
    }

    func connect(_ address: String) async throws {
        let dev = try device(address)
        if dev.isConnected() {
            Log.bluetooth.log("connect \(address): isConnected already true — skipping openConnection")
            return
        }
        // Synchronous open: blocks until the connection is fully established (HID up) and returns a
        // real result code. This is the reliable success signal — the async openConnection(_:)
        // callback proved unreliable (it sometimes never fires even when the link comes up).
        // The call internally spins the run loop, and always returns, so it can't hang `withTimeout`.
        Log.bluetooth.log("openConnection() \(address)…")
        let rc = dev.openConnection()
        Log.bluetooth.log("openConnection() \(address): rc=\(String(format: "0x%X", rc))")
        if rc != kIOReturnSuccess {
            throw BTError.connectFailed(rc)
        }
    }

    func disconnect(_ address: String) async throws {
        let dev = try device(address)
        guard dev.isConnected() else { return }
        let rc = dev.closeConnection()
        if rc != kIOReturnSuccess {
            throw BTError.disconnectFailed(rc)
        }
    }

    func pair(_ address: String) async throws {
        let dev = try device(address)
        if dev.isPaired() {
            Log.bluetooth.debug("pair: already paired, skipping")
            return
        }
        try await PairingHelper().pair(dev)
    }

    /// Private-API unpair via `-[IOBluetoothDevice remove]`.
    func unpair(_ address: String) async throws {
        let dev = try device(address)
        guard dev.isPaired() else { return }

        let sel = NSSelectorFromString("remove")
        guard dev.responds(to: sel),
            let method = class_getInstanceMethod(type(of: dev), sel)
        else {
            throw BTError.unpairUnavailable
        }
        typealias RemoveIMP = @convention(c) (AnyObject, Selector) -> Int32
        let imp = method_getImplementation(method)
        let fn = unsafeBitCast(imp, to: RemoveIMP.self)
        _ = fn(dev, sel)  // return value is unreliable (the selector effectively returns void)

        // Judge success by the actual pairing state rather than the bogus return value.
        if dev.isPaired() {
            Log.bluetooth.error("unpair \(address): device still paired after remove")
            throw BTError.unpairFailed(-1)
        }
        Log.bluetooth.log("unpair \(address): ok")
    }

    func pairedDevices() async -> [PairedDeviceInfo] {
        guard let devices = IOBluetoothDevice.pairedDevices() as? [IOBluetoothDevice] else { return [] }
        return devices.map {
            PairedDeviceInfo(
                name: $0.name ?? $0.addressString ?? "Unknown",
                address: $0.addressString ?? "")
        }
    }

    func isPoweredOn() async -> Bool? {
        guard let host = IOBluetoothHostController.default() else { return nil }
        return host.powerState == kBluetoothHCIPowerStateON
    }

    // MARK: - Monitoring (event-driven status)

    func startMonitoring(addresses: [String], onChange: @escaping (String, Bool) -> Void) {
        stopMonitoring()
        self.onChange = onChange
        self.monitoredAddresses = Set(addresses.map { $0.replacingOccurrences(of: ":", with: "-").lowercased() })

        // One global observer fires whenever ANY device connects.
        connectObserver = IOBluetoothDevice.register(
            forConnectNotifications: self,
            selector: #selector(deviceConnected(_:device:)))

        // Report current state for each monitored address, and arm a disconnect watch on any
        // that are already connected.
        for addr in monitoredAddresses {
            guard let dev = IOBluetoothDevice(addressString: addr) else { continue }
            let connected = dev.isConnected()
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
        }
    }

    @objc private func deviceConnected(_ note: IOBluetoothUserNotification, device: IOBluetoothDevice) {
        guard let addr = device.addressString?.lowercased(),
            monitoredAddresses.contains(addr)
        else { return }
        onChange?(addr, true)
        armDisconnect(device, addr)  // disconnect notifications are one-shot; re-arm each time
    }

    @objc private func deviceDisconnected(_ note: IOBluetoothUserNotification, device: IOBluetoothDevice) {
        guard let addr = device.addressString?.lowercased() else { return }
        disconnectObservers[addr]?.unregister()
        disconnectObservers[addr] = nil
        if monitoredAddresses.contains(addr) { onChange?(addr, false) }
    }
}

/// Drives an `IOBluetoothDevicePair` to completion. Auto-confirms secure-simple-pairing
/// (numeric-comparison) prompts — fine for HID devices like a trackpad/keyboard. Handles the full
/// delegate surface so failures report the step that broke; devices that demand a typed PIN can't
/// be paired headlessly and fail with a clear message.
@MainActor
private final class PairingHelper: NSObject, @preconcurrency IOBluetoothDevicePairDelegate {
    private var continuation: CheckedContinuation<Void, Error>?
    private var pairer: IOBluetoothDevicePair?

    @MainActor
    func pair(_ device: IOBluetoothDevice) async throws {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                self.continuation = cont
                guard let pairer = IOBluetoothDevicePair(device: device) else {
                    self.finish(.failure(BTError.pairFailed("could not create pairer")))
                    return
                }
                self.pairer = pairer
                pairer.delegate = self
                let rc = pairer.start()
                if rc != kIOReturnSuccess {
                    self.finish(.failure(BTError.pairFailed("could not start pairing (IOReturn \(rc))")))
                }
            }
        } onCancel: {
            Task { @MainActor in self.finish(.failure(BTError.timeout)) }
        }
    }

    private func finish(_ result: Result<Void, Error>) {
        pairer?.stop()
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
        Log.bluetooth.debug("pairing started")
    }

    func devicePairingConnecting(_ sender: Any!) {
        Log.bluetooth.debug("pairing: connecting")
    }

    /// Secure-simple-pairing numeric comparison — auto-accept (no UI for a background agent).
    func devicePairingUserConfirmationRequest(_ sender: Any!, numericValue: BluetoothNumericValue) {
        (sender as? IOBluetoothDevicePair)?.replyUserConfirmation(true)
    }

    /// Passkey the *device* displays; informational, no reply needed.
    func devicePairingUserPasskeyNotification(_ sender: Any!, passkey: BluetoothPasskey) {
        Log.bluetooth.debug("pairing: device passkey \(passkey)")
    }

    /// Legacy typed-PIN request. Modern SSP HID devices (trackpad/keyboard) shouldn't reach here;
    /// log it and let the attempt run its course rather than aborting a potentially-valid pairing.
    func devicePairingPINCodeRequest(_ sender: Any!) {
        Log.bluetooth.debug("pairing: PIN code requested (none supplied)")
    }

    func deviceSimplePairingComplete(_ sender: Any!, status: BluetoothHCIEventStatus) {
        if status != 0 {
            Log.bluetooth.error("simple pairing complete with status \(status)")
        }
    }

    func devicePairingFinished(_ sender: Any!, error: IOReturn) {
        if error == kIOReturnSuccess {
            finish(.success(()))
        } else {
            finish(.failure(BTError.pairFailed("pairing failed at completion (IOReturn \(error))")))
        }
    }
}
