import Foundation
import os

/// Per-category logger that writes to both `os.Logger` (Console.app / `log stream`) and the in-app
/// `DebugLog` buffer (viewable/exportable from Settings). Messages are plain `String`s.
struct CategoryLog {
    let logger: Logger
    let category: String

    func log(_ message: String) {
        logger.log("\(message, privacy: .public)")
        sink(message)
    }
    func debug(_ message: String) {
        logger.debug("\(message, privacy: .public)")
        sink(message)
    }
    func error(_ message: String) {
        logger.error("\(message, privacy: .public)")
        sink("ERROR: \(message)")
    }

    /// Mirror into the in-app buffer on the main actor (captures only Sendable values).
    private func sink(_ message: String) {
        let cat = category
        let date = Date()
        Task { @MainActor in DebugLog.shared.add(date: date, category: cat, message: message) }
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
