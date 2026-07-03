import Combine
import Foundation
import IOKit
import IOKit.usb

/// A USB device as seen by IOKit.
struct USBDeviceInfo: Identifiable, Hashable {
    var vendorID: UInt16
    var productID: UInt16
    var name: String

    var id: String { "\(vendorID)-\(productID)-\(name)" }
    var displayName: String {
        String(format: "%@  (0x%04X:0x%04X)", name.isEmpty ? "Unknown USB device" : name, vendorID, productID)
    }
}

/// Watches USB attach/detach via IOKit and exposes a live snapshot of attached devices.
///
/// `attachedDevices` is kept current for the settings dropdown. `events` fires on every
/// add/remove so the SelectionEngine can react.
@MainActor
final class USBMonitor: ObservableObject, USBMonitoring {
    @Published private(set) var attachedDevices: [USBDeviceInfo] = []

    /// (vendorID, productID, added?) — added == true for attach, false for detach.
    let events = PassthroughSubject<(vendorID: UInt16, productID: UInt16, added: Bool), Never>()

    /// Legacy class name still matches USB devices on modern macOS via the compatibility layer.
    private static let usbDeviceClass = "IOUSBDevice"

    private var notifyPort: IONotificationPortRef?
    private var addedIterator: io_iterator_t = 0
    private var removedIterator: io_iterator_t = 0
    private var reconcileTimer: Timer?

    func start() {
        guard notifyPort == nil else { return }

        guard let port = IONotificationPortCreate(kIOMainPortDefault) else { return }
        notifyPort = port
        IONotificationPortSetDispatchQueue(port, DispatchQueue.main)

        let matching = IOServiceMatching(Self.usbDeviceClass)

        // Retain self via Unmanaged for the C callback's refcon.
        let selfPtr = Unmanaged.passUnretained(self).toOpaque()

        // --- Added ---
        var addIter: io_iterator_t = 0
        let addRC = IOServiceAddMatchingNotification(
            port, kIOFirstMatchNotification, matching,
            { (refcon, iterator) in
                // Callbacks are dispatched on the main queue (see IONotificationPortSetDispatchQueue).
                MainActor.assumeIsolated {
                    let mon = Unmanaged<USBMonitor>.fromOpaque(refcon!).takeUnretainedValue()
                    mon.handleIterator(iterator, added: true)
                }
            },
            selfPtr, &addIter)
        if addRC != KERN_SUCCESS {
            Log.usb.error("IOServiceAddMatchingNotification (attach) failed: \(addRC) — relying on the reconcile timer")
        }
        addedIterator = addIter
        handleIterator(addIter, added: true)  // drain initial matches + seed

        // --- Removed ---
        // IOServiceMatching is consumed by each call, so build a fresh dictionary.
        let matchingRemove = IOServiceMatching(Self.usbDeviceClass)
        var remIter: io_iterator_t = 0
        let removeRC = IOServiceAddMatchingNotification(
            port, kIOTerminatedNotification, matchingRemove,
            { (refcon, iterator) in
                MainActor.assumeIsolated {
                    let mon = Unmanaged<USBMonitor>.fromOpaque(refcon!).takeUnretainedValue()
                    mon.handleIterator(iterator, added: false)
                }
            },
            selfPtr, &remIter)
        if removeRC != KERN_SUCCESS {
            Log.usb.error(
                "IOServiceAddMatchingNotification (detach) failed: \(removeRC) — relying on reconcile")
        }
        removedIterator = remIter
        drain(remIter)  // arm the notification (initial set are already-present devices)

        refreshAttached()

        // Safety-net reconcile (spec §4): a missed or property-less kIOTerminatedNotification
        // would leave presence stale until the next event or wake. Compare a fresh enumeration
        // every 2 s and emit synthetic events for anything the notifications missed.
        // (.common mode so it keeps running while a menu/modal is open.)
        let timer = Timer(timeInterval: 2.0, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.reconcile() }
        }
        RunLoop.main.add(timer, forMode: .common)
        reconcileTimer = timer
    }

    func stop() {
        reconcileTimer?.invalidate()
        reconcileTimer = nil
        if addedIterator != 0 { IOObjectRelease(addedIterator); addedIterator = 0 }
        if removedIterator != 0 { IOObjectRelease(removedIterator); removedIterator = 0 }
        if let port = notifyPort { IONotificationPortDestroy(port); notifyPort = nil }
    }

    /// Public, on-demand enumeration for the settings dropdown.
    func refreshAttached() {
        var result: [USBDeviceInfo] = []
        var iter: io_iterator_t = 0
        let matching = IOServiceMatching(Self.usbDeviceClass)
        if IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iter) == KERN_SUCCESS {
            while case let service = IOIteratorNext(iter), service != 0 {
                if let info = Self.deviceInfo(from: service) { result.append(info) }
                IOObjectRelease(service)
            }
            IOObjectRelease(iter)
        }
        // De-dup by vid/pid/name, keep stable order by name.
        let unique = Array(Set(result)).sorted { $0.name < $1.name }
        attachedDevices = unique
    }

    // MARK: - Private

    private func handleIterator(_ iterator: io_iterator_t, added: Bool) {
        while case let service = IOIteratorNext(iterator), service != 0 {
            if let info = Self.deviceInfo(from: service) {
                events.send((info.vendorID, info.productID, added))
            }
            IOObjectRelease(service)
        }
        refreshAttached()
    }

    private func drain(_ iterator: io_iterator_t) {
        while case let service = IOIteratorNext(iterator), service != 0 {
            IOObjectRelease(service)
        }
    }

    private static func presenceKey(_ d: USBDeviceInfo) -> UInt32 {
        (UInt32(d.vendorID) << 16) | UInt32(d.productID)
    }

    /// Diff a fresh enumeration against the last snapshot and emit synthetic attach/detach
    /// events for anything the IOKit notifications missed.
    private func reconcile() {
        let before = Set(attachedDevices.map(Self.presenceKey))
        refreshAttached()
        let after = Set(attachedDevices.map(Self.presenceKey))
        guard before != after else { return }

        Log.usb.log("reconcile: presence drifted (\(before.count) → \(after.count) ids); emitting corrections")
        for key in before.subtracting(after) {
            events.send((vendorID: UInt16(key >> 16), productID: UInt16(key & 0xFFFF), added: false))
        }
        for key in after.subtracting(before) {
            events.send((vendorID: UInt16(key >> 16), productID: UInt16(key & 0xFFFF), added: true))
        }
    }

    private static func deviceInfo(from service: io_object_t) -> USBDeviceInfo? {
        func intProp(_ key: String) -> Int? {
            guard
                let ref = IORegistryEntryCreateCFProperty(service, key as CFString, kCFAllocatorDefault, 0)?
                    .takeRetainedValue()
            else { return nil }
            return (ref as? NSNumber)?.intValue
        }
        func strProp(_ key: String) -> String? {
            guard
                let ref = IORegistryEntryCreateCFProperty(service, key as CFString, kCFAllocatorDefault, 0)?
                    .takeRetainedValue()
            else { return nil }
            return ref as? String
        }

        guard let vid = intProp("idVendor"), let pid = intProp("idProduct") else { return nil }
        let name = strProp("USB Product Name") ?? strProp("kUSBProductString") ?? strProp("Product Name") ?? ""
        return USBDeviceInfo(
            vendorID: UInt16(truncatingIfNeeded: vid),
            productID: UInt16(truncatingIfNeeded: pid),
            name: name)
    }
}
