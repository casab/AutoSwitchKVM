import Foundation
import os

/// Persistent daily log file: `~/Library/Logs/AutoSwitchKVM/app-yyyyMMdd.log`, the macOS
/// counterpart of the Windows app's `logs\app-yyyyMMdd.log`.
///
/// Why a file and not just `os.Logger`: the unified log is not a reliable post-mortem record for
/// this app. A real run on 2026-11-11 left none of its own lines in the log store (Apple framework
/// lines from the same process were kept), and the in-app `DebugLog` buffer dies with the process.
/// Handoff bugs are diagnosed from logs of several switches, so they must survive a restart.
///
/// Disabled until `start(directory:)` is called (the app calls it at launch), so unit tests never
/// write into the user's log folder. Thread-safe: all state is confined to a serial queue, and
/// `write` may be called from any thread.
final class FileLog: @unchecked Sendable {
    static let shared = FileLog()

    static var defaultDirectory: URL {
        FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Logs", isDirectory: true)
            .appendingPathComponent("AutoSwitchKVM", isDirectory: true)
    }

    private let queue = DispatchQueue(label: "com.enginal.AutoSwitchKVM.filelog")
    /// Reports FileLog's own failures. Deliberately not `Log.*` (that would recurse into FileLog).
    private let selfLogger = Logger(subsystem: "com.enginal.AutoSwitchKVM", category: "filelog")

    // All mutable state below is only touched on `queue`.
    private var directory: URL?
    private var handle: FileHandle?
    private var currentDay = ""
    private var currentURL: URL?
    private var reportedFailure = false
    private let dayFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyyMMdd"
        return f
    }()
    private let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "HH:mm:ss.SSS"
        return f
    }()

    /// Internal so tests can use a private instance with a temporary directory.
    init() {}

    /// Enable file logging into `directory`, and delete `app-*.log` files older than
    /// `retentionDays`. Safe to call again (e.g. to switch directories in tests).
    func start(directory: URL = FileLog.defaultDirectory, retentionDays: Int = 14) {
        queue.sync {
            closeHandle()
            self.directory = directory
            do {
                try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            } catch {
                reportFailure("cannot create log directory \(directory.path): \(error)")
            }
            prune(olderThanDays: retentionDays)
        }
    }

    /// Append one line. `level` is a short tag (INFO/WARN/ERROR/DEBUG); the date is captured by
    /// the caller so lines keep their real order even though writing is asynchronous.
    func write(level: String, category: String, message: String, date: Date = Date()) {
        queue.async { [self] in
            guard directory != nil, let handle = handleForDay(of: date) else { return }
            let padded = level.padding(toLength: 5, withPad: " ", startingAt: 0)
            let line = "\(timeFormatter.string(from: date))  \(padded)  [\(category)]  \(message)\n"
            do {
                try handle.write(contentsOf: Data(line.utf8))
            } catch {
                reportFailure("writing to \(currentURL?.path ?? "?") failed: \(error)")
                closeHandle()  // reopen on the next line
            }
        }
    }

    /// Block until queued lines are written (tests, and on quit).
    func flush() {
        queue.sync { try? handle?.synchronize() }
    }

    /// The file currently being written (nil until the first line after `start`).
    var fileURL: URL? { queue.sync { currentURL } }

    /// The log folder (nil while disabled).
    var folderURL: URL? { queue.sync { directory } }

    // MARK: - Queue-confined helpers

    private func handleForDay(of date: Date) -> FileHandle? {
        let day = dayFormatter.string(from: date)
        if let handle, day == currentDay { return handle }
        closeHandle()
        guard let directory else { return nil }
        let url = directory.appendingPathComponent("app-\(day).log")
        let fm = FileManager.default
        if !fm.fileExists(atPath: url.path), !fm.createFile(atPath: url.path, contents: nil) {
            reportFailure("cannot create \(url.path)")
            return nil
        }
        do {
            let h = try FileHandle(forWritingTo: url)
            try h.seekToEnd()
            handle = h
            currentDay = day
            currentURL = url
            return h
        } catch {
            reportFailure("cannot open \(url.path): \(error)")
            return nil
        }
    }

    private func closeHandle() {
        try? handle?.close()
        handle = nil
        currentDay = ""
    }

    private func prune(olderThanDays days: Int) {
        guard let directory, days > 0 else { return }
        let fm = FileManager.default
        let cutoff = Date().addingTimeInterval(-Double(days) * 86_400)
        let files =
            (try? fm.contentsOfDirectory(
                at: directory, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        for url in files where url.lastPathComponent.hasPrefix("app-") && url.pathExtension == "log" {
            let modified = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
            guard let modified, modified < cutoff else { continue }
            do {
                try fm.removeItem(at: url)
            } catch {
                reportFailure("cannot delete old log \(url.lastPathComponent): \(error)")
            }
        }
    }

    /// Report FileLog's own failure once per enable, so a broken disk doesn't spam the log.
    private func reportFailure(_ message: String) {
        guard !reportedFailure else { return }
        reportedFailure = true
        selfLogger.error("file logging problem: \(message, privacy: .public)")
    }
}

/// Formats elapsed time for log lines: "85 ms" below 10 s, "12.3 s" above (matches the Windows log).
enum Elapsed {
    static func format(_ d: Duration) -> String {
        let (seconds, attoseconds) = d.components
        let ms = Double(seconds) * 1_000 + Double(attoseconds) / 1e15
        if ms < 10_000 { return "\(Int(ms.rounded())) ms" }
        return String(format: "%.1f s", ms / 1_000)
    }

    static func since(_ start: ContinuousClock.Instant) -> String {
        format(start.duration(to: .now))
    }
}
