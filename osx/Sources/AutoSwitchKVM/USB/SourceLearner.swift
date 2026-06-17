import Foundation
import Combine

/// Detects the USB device(s) that constitute a KVM source by watching what changes while the
/// user switches the KVM. Snapshot the attached devices at start; any device that appears or
/// disappears during the window becomes a candidate the user then reviews and names.
@MainActor
final class SourceLearner: ObservableObject {
    @Published private(set) var isLearning = false
    @Published private(set) var changeCount = 0

    private let usb: USBMonitoring
    private var cancellable: AnyCancellable?
    private var baseline: [UInt32: USBDeviceInfo] = [:]
    private var changedKeys = Set<UInt32>()

    init(usb: USBMonitoring) { self.usb = usb }

    private func key(_ vendorID: UInt16, _ productID: UInt16) -> UInt32 {
        (UInt32(vendorID) << 16) | UInt32(productID)
    }
    private func key(_ d: USBDeviceInfo) -> UInt32 { key(d.vendorID, d.productID) }

    func start() {
        baseline = Dictionary(usb.attachedDevices.map { (key($0), $0) }, uniquingKeysWith: { a, _ in a })
        changedKeys = []
        changeCount = 0
        isLearning = true
        cancellable = usb.events.sink { [weak self] event in
            guard let self else { return }
            self.changedKeys.insert(self.key(event.vendorID, event.productID))
            self.changeCount = self.changedKeys.count
        }
    }

    /// Stop learning and return the candidate devices (appeared or disappeared during the window),
    /// with names resolved from the current attached list or the start-of-window snapshot.
    func finish() -> [USBDeviceInfo] {
        isLearning = false
        cancellable?.cancel(); cancellable = nil

        let current = Dictionary(usb.attachedDevices.map { (key($0), $0) }, uniquingKeysWith: { a, _ in a })
        var result: [USBDeviceInfo] = []
        for k in changedKeys {
            if let info = current[k] ?? baseline[k] {
                result.append(info)
            } else {
                result.append(USBDeviceInfo(vendorID: UInt16(k >> 16),
                                            productID: UInt16(k & 0xFFFF),
                                            name: ""))
            }
        }
        return result.sorted { $0.displayName < $1.displayName }
    }

    func cancel() {
        isLearning = false
        cancellable?.cancel(); cancellable = nil
        changedKeys = []
        changeCount = 0
    }
}
