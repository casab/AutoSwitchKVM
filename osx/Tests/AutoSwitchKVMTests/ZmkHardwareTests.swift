import XCTest

@testable import AutoSwitchKVM

/// Hardware check for `ZmkHostSwitcher` (read-only: GET_INFO only, never switches or marks).
/// Skipped unless `AUTOSWITCH_ZMK_ADDRESS` names a ZMK keyboard with the AutoSwitch module that is
/// connected to this Mac:
///
///     AUTOSWITCH_ZMK_ADDRESS=xx-xx-xx-xx-xx-xx swift test --filter ZmkHardwareTests
@MainActor
final class ZmkHardwareTests: XCTestCase {
    func testReadsTheProfilesOfAConnectedKeyboard() async throws {
        guard let address = ProcessInfo.processInfo.environment["AUTOSWITCH_ZMK_ADDRESS"], !address.isEmpty else {
            throw XCTSkip("set AUTOSWITCH_ZMK_ADDRESS to run against real hardware")
        }
        let switcher = ZmkHostSwitcher()
        let first = try await switcher.channels(address)
        let ch = try XCTUnwrap(first, "no GET_INFO answer (connected to this Mac, module flashed?)")
        XCTAssertGreaterThanOrEqual(ch.count, 2)
        XCTAssertLessThan(ch.current, ch.count)
        XCTAssertEqual(ch.marks.count, ch.count)
        XCTAssertEqual(ch.paired?.count, ch.count)
        XCTAssertEqual(ch.paired?[ch.current], true, "this Mac's profile must be paired")
        // Through the composite, as the app uses it: the Logitech backend doesn't answer, ZMK does.
        let composite = CompositeHostSwitcher([LogitechHostSwitcher(), switcher])
        let viaComposite = try await composite.channels(address)
        XCTAssertEqual(viaComposite, ch)
        // The Mac sees the LE keyboard: connected (its HID services are registered) and listed
        // among the paired devices (IOBluetooth's own list is Classic-only).
        XCTAssertTrue(IOBluetoothController.lowEnergyHIDRegistered(address))
        let paired = await DeviceIdentityReader.systemPairedDevices()
        XCTAssertTrue(
            paired.contains { DeviceIdentityReader.normalize($0.address) == DeviceIdentityReader.normalize(address) },
            "\(paired)")
        print("HW paired list=\(paired.map { "\($0.name) \($0.address)" })")
        print(
            "HW zmk profiles=\(ch.count) current=\(ch.current) paired=\(ch.paired ?? []) marks=\(ch.marks) "
                + "automatic=\(String(describing: ch.automaticTarget))")
    }
}
