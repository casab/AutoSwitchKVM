import XCTest

@testable import AutoSwitchKVM

final class ModelsTests: XCTestCase {

    func testAddressNormalization() {
        let colon = BTDevice(name: "x", address: "3C:50:02:BF:22:45")
        XCTAssertEqual(colon.normalizedAddress, "3c-50-02-bf-22-45")

        let dash = BTDevice(name: "x", address: "3C-50-02-BF-22-45")
        XCTAssertEqual(dash.normalizedAddress, "3c-50-02-bf-22-45")
    }

    func testSourceDisplayVidPid() {
        let source = USBSource(name: "Hub", vendorID: 0x05E3, productIDs: [0x0626, 0x0610])
        // Product IDs are sorted in the display string.
        XCTAssertEqual(source.displayVidPid, "0x05E3 : 0x0610, 0x0626")
    }

    func testDeviceStatusLabels() {
        XCTAssertEqual(DeviceStatus.connected.label, "Connected")
        XCTAssertEqual(DeviceStatus.error("boom").label, "Error: boom")
    }
}
