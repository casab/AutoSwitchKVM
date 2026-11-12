import Combine
import Foundation

/// Abstraction over USB attach/detach so the SelectionEngine can be unit-tested with a fake
/// event stream instead of live IOKit hardware.
@MainActor
protocol USBMonitoring: AnyObject {
    var attachedDevices: [USBDeviceInfo] { get }
    var events: PassthroughSubject<USBEvent, Never> { get }
    func refreshAttached()
}
