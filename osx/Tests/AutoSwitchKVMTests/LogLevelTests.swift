import XCTest

@testable import AutoSwitchKVM

/// Spec §10: `logLevel` (default info, unknown = info) and the threshold that every sink obeys.
@MainActor
final class LogLevelTests: XCTestCase {
    private var saved: LogLevel = .info

    override func setUp() async throws {
        saved = LogThreshold.shared.level
    }

    override func tearDown() async throws {
        LogThreshold.shared.level = saved
    }

    func testMissingOrUnknownLevelDecodesAsInfo() throws {
        let missing = try JSONDecoder().decode(AppConfig.self, from: Data("{}".utf8))
        XCTAssertEqual(missing.logLevel, .info)
        let unknown = try JSONDecoder().decode(AppConfig.self, from: Data(#"{"logLevel":"verbose"}"#.utf8))
        XCTAssertEqual(unknown.logLevel, .info, "an unknown value must not fail the whole config")
        let warning = try JSONDecoder().decode(AppConfig.self, from: Data(#"{"logLevel":"warning"}"#.utf8))
        XCTAssertEqual(warning.logLevel, .warning)
    }

    func testLevelRoundTripsAsTheSpecString() throws {
        var cfg = AppConfig()
        cfg.logLevel = .error
        let json = try XCTUnwrap(
            JSONSerialization.jsonObject(with: JSONEncoder().encode(cfg)) as? [String: Any])
        XCTAssertEqual(json["logLevel"] as? String, "error")
    }

    func testThresholdOrdering() {
        LogThreshold.shared.level = .info
        XCTAssertTrue(LogThreshold.shared.allows(.error))
        XCTAssertTrue(LogThreshold.shared.allows(.warning))
        XCTAssertTrue(LogThreshold.shared.allows(.info))
        XCTAssertFalse(LogThreshold.shared.allows(.debug))

        LogThreshold.shared.level = .error
        XCTAssertFalse(LogThreshold.shared.allows(.warning))
        XCTAssertTrue(LogThreshold.shared.allows(.error))
    }

    /// Lines below the level never reach the in-app buffer (the file log uses the same gate).
    func testLinesBelowTheLevelAreDropped() async {
        let marker = UUID().uuidString
        LogThreshold.shared.level = .warning
        Log.engine.debug("debug \(marker)")
        Log.engine.log("info \(marker)")
        Log.engine.warn("warn \(marker)")
        Log.engine.always("always \(marker)")
        for _ in 0..<20 { await Task.yield() }

        let lines = DebugLog.shared.entries.map(\.message).filter { $0.contains(marker) }
        XCTAssertEqual(lines.count, 2, "\(lines)")
        XCTAssertTrue(lines.contains { $0.hasPrefix("WARN: warn") })
        XCTAssertTrue(lines.contains { $0.hasPrefix("always") })
    }

    // MARK: - Log budget for a handoff

    /// Runs one arrive + leave with a managed trackpad and returns the lines it logged.
    private func handoffLines(at level: LogLevel) async -> [String] {
        LogThreshold.shared.level = level
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigStore(directory: tmp)
        store.config.source = USBSource(name: "Hub", vendorID: 0x05E3, productIDs: [0x0626])
        store.config.devices = [
            BTDevice(name: "Trackpad", address: "3c-50-02-bf-22-45", managePairing: true, connectDelayMs: 0)
        ]
        let bt = MockBluetoothController()
        bt.pairLeavesLinkUp = true  // like hardware: HID comes up on the pairing link
        let engine = SelectionEngine(store: store, usb: MockUSBMonitor(), bt: bt)
        engine.sleepHook = { _ in }

        for _ in 0..<20 { await Task.yield() }
        let before = DebugLog.shared.entries.count
        engine.handleUSB((vendorID: 0x05E3, productID: 0x0626, added: true))
        await engine.evaluateNow()
        engine.handleUSB((vendorID: 0x05E3, productID: 0x0626, added: false))
        await engine.evaluateNow()
        for _ in 0..<20 { await Task.yield() }
        return DebugLog.shared.entries.dropFirst(before).map(\.message)
    }

    /// At the default level a handoff is the story, not the steps: about one line per event.
    func testANormalHandoffLogsOnlyTheStory() async {
        let normal = await handoffLines(at: .info)
        XCTAssertLessThanOrEqual(normal.count, 6, "info should read like a story:\n\(normal.joined(separator: "\n"))")
        XCTAssertTrue(normal.contains { $0.contains("connected ✓") }, "\(normal)")
        XCTAssertTrue(normal.contains { $0.contains("released (unpaired)") }, "\(normal)")

        let detailed = await handoffLines(at: .debug)
        XCTAssertGreaterThan(detailed.count, normal.count, "debug adds the step detail")

        let quiet = await handoffLines(at: .warning)
        XCTAssertEqual(quiet, [], "a clean handoff logs nothing at warning")
    }
}
