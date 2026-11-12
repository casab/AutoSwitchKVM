import XCTest

@testable import AutoSwitchKVM

/// Mirrors windows/tests SourceLearnerTests: appeared/disappeared devices become candidates,
/// steady devices are ignored, cancel clears state.
@MainActor
final class SourceLearnerTests: XCTestCase {

    func testDetectsAppearedAndDisappearedDevicesIgnoringSteadyOnes() {
        let usb = MockUSBMonitor()
        let steady = USBDeviceInfo(vendorID: 0x1111, productID: 0x0001, name: "Steady Mouse")
        let leaving = USBDeviceInfo(vendorID: 0x05E3, productID: 0x0626, name: "Hub A")
        usb.attachedDevices = [steady, leaving]

        let learner = SourceLearner(usb: usb)
        learner.start()
        XCTAssertTrue(learner.isLearning)

        // The KVM switch: one hub instance disappears, another appears; the mouse never changes.
        usb.events.send(USBEvent(vendorID: 0x05E3, productID: 0x0626, locationID: nil, added: false))
        usb.events.send(USBEvent(vendorID: 0x05E3, productID: 0x0610, locationID: nil, added: true))
        usb.attachedDevices = [steady, USBDeviceInfo(vendorID: 0x05E3, productID: 0x0610, name: "Hub B")]

        XCTAssertEqual(learner.changeCount, 2)
        let candidates = learner.finish()
        XCTAssertEqual(Set(candidates.map(\.productID)), [0x0626, 0x0610])
        XCTAssertFalse(candidates.contains { $0.vendorID == 0x1111 }, "steady devices are not candidates")
        XCTAssertFalse(learner.isLearning)

        // The disappeared device's name resolves from the start-of-window snapshot.
        XCTAssertEqual(candidates.first { $0.productID == 0x0626 }?.name, "Hub A")
    }

    func testCancelClearsState() {
        let usb = MockUSBMonitor()
        let learner = SourceLearner(usb: usb)
        learner.start()
        usb.events.send(USBEvent(vendorID: 0x05E3, productID: 0x0626, locationID: nil, added: false))
        XCTAssertEqual(learner.changeCount, 1)

        learner.cancel()

        XCTAssertEqual(learner.changeCount, 0)
        XCTAssertFalse(learner.isLearning)
    }
}
