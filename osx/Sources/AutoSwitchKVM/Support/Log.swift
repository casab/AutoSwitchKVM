import Foundation
import os

/// How much the app logs (`AppConfig.logLevel`, spec §10). Ordered from least to most detail.
/// The same policy as the Windows app, so the two logs read alike:
/// - `error`: something failed and needs attention (release failed, gave up, config save failed).
/// - `warning`: recovered or worked around (a hard connect failure, a fallback, a failed query).
/// - `info` (default): the handoff story and user actions, about one line per event.
/// - `debug`: step detail (per-try steps and timings, raw Bluetooth calls, waits, USB detail,
///   superseded runs, expected refusals).
enum LogLevel: String, Codable, CaseIterable, Sendable, Comparable {
    case error, warning, info, debug

    private var rank: Int {
        switch self {
        case .error: return 0
        case .warning: return 1
        case .info: return 2
        case .debug: return 3
        }
    }

    static func < (a: LogLevel, b: LogLevel) -> Bool { a.rank < b.rank }

    /// Missing or unknown values mean `info`, so old and foreign configs behave as before.
    init(config value: String?) { self = value.flatMap(LogLevel.init(rawValue:)) ?? .info }

    var title: String {
        switch self {
        case .error: return "Errors only"
        case .warning: return "Warnings"
        case .info: return "Normal"
        case .debug: return "Detailed"
        }
    }
}

/// The process-wide minimum level. Thread-safe: logging happens from any thread.
final class LogThreshold: @unchecked Sendable {
    static let shared = LogThreshold()
    private let lock = NSLock()
    private var current: LogLevel = .info

    var level: LogLevel {
        get { lock.withLock { current } }
        set { lock.withLock { current = newValue } }
    }

    func allows(_ level: LogLevel) -> Bool { level <= self.level }
}

/// Per-category logger that writes to `os.Logger` (Console.app / `log stream`), the persistent
/// `FileLog` (`~/Library/Logs/AutoSwitchKVM/app-yyyyMMdd.log`), and the in-app `DebugLog` buffer
/// (viewable/exportable from Settings ▸ Diagnostics). Messages are plain `String`s.
///
/// Levels follow `LogLevel`: `error`, `warn`, `log` (info) and `debug`; lines below the configured
/// `LogThreshold` are dropped from every sink. `always` writes regardless of the level (a change
/// of the level itself).
struct CategoryLog: Sendable {
    let logger: Logger
    let category: String

    func log(_ message: String) {
        guard LogThreshold.shared.allows(.info) else { return }
        logger.log("\(message, privacy: .public)")
        emit("INFO", message, buffered: message)
    }
    func debug(_ message: String) {
        guard LogThreshold.shared.allows(.debug) else { return }
        logger.debug("\(message, privacy: .public)")
        emit("DEBUG", message, buffered: message)
    }
    func warn(_ message: String) {
        guard LogThreshold.shared.allows(.warning) else { return }
        logger.warning("\(message, privacy: .public)")
        emit("WARN", message, buffered: "WARN: \(message)")
    }
    func error(_ message: String) {
        logger.error("\(message, privacy: .public)")
        emit("ERROR", message, buffered: "ERROR: \(message)")
    }
    /// Written at every level.
    func always(_ message: String) {
        logger.log("\(message, privacy: .public)")
        emit("INFO", message, buffered: message)
    }

    /// Mirror into the file log (date captured now, so asynchronous writes keep the real order)
    /// and into the in-app buffer on the main actor (captures only Sendable values).
    private func emit(_ level: String, _ message: String, buffered: String) {
        let cat = category
        let date = Date()
        FileLog.shared.write(level: level, category: cat, message: message, date: date)
        Task { @MainActor in DebugLog.shared.add(date: date, category: cat, message: buffered) }
    }
}

/// Unified logging. Subsystem `com.enginal.AutoSwitchKVM`, categories `app`/`usb`/`bluetooth`/`engine`.
/// Use these instead of `NSLog`/`print`.
enum Log {
    private static let subsystem = "com.enginal.AutoSwitchKVM"

    static let app = CategoryLog(logger: Logger(subsystem: subsystem, category: "app"), category: "app")
    static let usb = CategoryLog(logger: Logger(subsystem: subsystem, category: "usb"), category: "usb")
    static let bluetooth = CategoryLog(
        logger: Logger(subsystem: subsystem, category: "bluetooth"), category: "bluetooth")
    static let engine = CategoryLog(logger: Logger(subsystem: subsystem, category: "engine"), category: "engine")
}
