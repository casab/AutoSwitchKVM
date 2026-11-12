import XCTest

@testable import AutoSwitchKVM

/// The lock that keeps a second copy of the app from driving the same devices.
@MainActor
final class SingleInstanceTests: XCTestCase {
    func testTheLockIsExclusiveWhileHeld() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        XCTAssertTrue(SingleInstance.claim(in: dir), "the first copy gets the lock")

        // Another copy: a separate open file description on the same lock file.
        let fd = open(dir.appendingPathComponent("instance.lock").path, O_RDWR)
        XCTAssertGreaterThanOrEqual(fd, 0)
        defer { close(fd) }
        XCTAssertNotEqual(flock(fd, LOCK_EX | LOCK_NB), 0, "a second copy must not get the lock")
        XCTAssertEqual(errno, EWOULDBLOCK)
        XCTAssertFalse(SingleInstance.claim(in: dir), "claim reports the lock as taken")
    }
}
