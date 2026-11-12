import Combine
import Foundation

/// Detects the USB device(s) that constitute a KVM source by watching what changes while the
/// user switches the KVM. Snapshot the attached devices at start; any device that appears or
/// disappears during the window becomes a candidate the user then reviews and names.
///
/// Devices are tracked individually (IDs + bus location), not just by IDs: a dock can carry a
/// permanent hub with the same IDs as the KVM's switching hub, and only the switching one may
/// become the source (seen on hardware 2026-11-12).
@MainActor
final class SourceLearner: ObservableObject {
    @Published private(set) var isLearning = false
    @Published private(set) var changeCount = 0

    private struct Key: Hashable {
        let vendorID: UInt16
        let productID: UInt16
        let locationID: UInt32?
    }

    private let usb: USBMonitoring
    private var cancellable: AnyCancellable?
    private var baseline: [Key: USBDeviceInfo] = [:]
    private var changedKeys = Set<Key>()

    init(usb: USBMonitoring) { self.usb = usb }

    private func key(_ d: USBDeviceInfo) -> Key {
        Key(vendorID: d.vendorID, productID: d.productID, locationID: d.locationID)
    }

    func start() {
        baseline = Dictionary(usb.attachedDevices.map { (key($0), $0) }, uniquingKeysWith: { a, _ in a })
        changedKeys = []
        changeCount = 0
        isLearning = true
        Log.usb.log("learn: started with \(baseline.count) USB device(s) attached")
        cancellable = usb.events.sink { [weak self] event in
            guard let self else { return }
            let k = Key(vendorID: event.vendorID, productID: event.productID, locationID: event.locationID)
            self.changedKeys.insert(k)
            self.changeCount = self.changedKeys.count
        }
    }

    /// Stop learning and return the candidate devices (appeared or disappeared during the window),
    /// with names resolved from the current attached list or the start-of-window snapshot.
    func finish() -> [USBDeviceInfo] {
        isLearning = false
        cancellable?.cancel()
        cancellable = nil

        let current = Dictionary(usb.attachedDevices.map { (key($0), $0) }, uniquingKeysWith: { a, _ in a })
        let result = changedKeys.map { k in
            current[k] ?? baseline[k]
                ?? USBDeviceInfo(vendorID: k.vendorID, productID: k.productID, name: "", locationID: k.locationID)
        }
        .sorted { $0.displayName < $1.displayName }
        Log.usb.log("learn: finished; candidates: \(result.map(\.displayName).joined(separator: "; "))")
        return result
    }

    func cancel() {
        isLearning = false
        cancellable?.cancel()
        cancellable = nil
        changedKeys = []
        changeCount = 0
    }
}
