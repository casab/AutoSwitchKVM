import XCTest

@testable import AutoSwitchKVM

/// Hardware check for `LogitechHostSwitcher` (read-only: never switches the device). Skipped
/// unless `AUTOSWITCH_LOGITECH_ADDRESS` names a Logitech Easy-Switch device connected to this Mac:
///
///     AUTOSWITCH_LOGITECH_ADDRESS=xx-xx-xx-xx-xx-xx swift test --filter LogitechHardwareTests
@MainActor
final class LogitechHardwareTests: XCTestCase {
    func testReadsTheChannelsOfAConnectedDevice() async throws {
        guard let address = ProcessInfo.processInfo.environment["AUTOSWITCH_LOGITECH_ADDRESS"], !address.isEmpty
        else {
            throw XCTSkip("set AUTOSWITCH_LOGITECH_ADDRESS to run against real hardware")
        }
        let switcher = LogitechHostSwitcher()
        let first = try await switcher.channels(address)
        let ch = try XCTUnwrap(first, "no Change Host answer (connected to this Mac?)")
        XCTAssertGreaterThanOrEqual(ch.count, 2)
        XCTAssertLessThan(ch.current, ch.count)
        XCTAssertEqual(ch.marks.count, ch.count)
        // Again, with the feature index cached.
        let second = try await switcher.channels(address)
        XCTAssertEqual(second, ch)
        // Hosts Info names (read-only); nil when the device doesn't keep names.
        let names = try await switcher.channelNames(address)
        if let names { XCTAssertEqual(names.count, ch.count) }
        print("HW channel names=\(String(describing: names))")
        print(
            "HW channels=\(ch.count) current=\(ch.current) marks=\(ch.marks) other=\(String(describing: ch.markedOtherChannel))"
        )
    }
}
