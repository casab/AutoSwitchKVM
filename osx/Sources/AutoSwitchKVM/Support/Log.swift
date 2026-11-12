import Foundation
import os

/// Per-category logger that writes to `os.Logger` (Console.app / `log stream`), the persistent
/// `FileLog` (`~/Library/Logs/AutoSwitchKVM/app-yyyyMMdd.log`), and the in-app `DebugLog` buffer
/// (viewable/exportable from Settings ▸ Diagnostics). Messages are plain `String`s.
///
/// Levels: `log` = normal flow (persisted), `warn` = something failed but the flow continues
/// (a best-effort step, a retry), `error` = an operation failed for good. `debug` is for noise
/// that is only useful while developing.
struct CategoryLog: Sendable {
    let logger: Logger
    let category: String

    func log(_ message: String) {
        logger.log("\(message, privacy: .public)")
        emit("INFO", message, buffered: message)
    }
    func debug(_ message: String) {
        logger.debug("\(message, privacy: .public)")
        emit("DEBUG", message, buffered: message)
    }
    func warn(_ message: String) {
        logger.warning("\(message, privacy: .public)")
        emit("WARN", message, buffered: "WARN: \(message)")
    }
    func error(_ message: String) {
        logger.error("\(message, privacy: .public)")
        emit("ERROR", message, buffered: "ERROR: \(message)")
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
