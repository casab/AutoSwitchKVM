import Combine
import Foundation

/// Loads and persists `AppConfig` as JSON in Application Support, and publishes changes.
/// Every load/save problem is logged (category `app`): a silently failed save loses the user's
/// settings at the next launch.
@MainActor
final class ConfigStore: ObservableObject {
    @Published var config: AppConfig {
        didSet { scheduleSave() }
    }

    private let fileURL: URL
    private var saveWorkItem: DispatchWorkItem?

    /// - Parameter directory: where to store `config.json`. Defaults to Application Support;
    ///   tests pass a temporary directory to avoid touching the real config.
    init(directory: URL? = nil) {
        let fm = FileManager.default
        let dir =
            directory
            ?? fm.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("AutoSwitchKVM", isDirectory: true)
        do {
            try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        } catch {
            Log.app.error("cannot create the config directory \(dir.path): \(error)")
        }
        self.fileURL = dir.appendingPathComponent("config.json")
        self.config = Self.load(from: fileURL)
    }

    private static func load(from url: URL) -> AppConfig {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else {
            Log.app.log("no config at \(url.path); starting with defaults")
            return .default
        }
        let data: Data
        do {
            data = try Data(contentsOf: url)
        } catch {
            Log.app.error("cannot read \(url.path): \(error); starting with defaults (the file is left as is)")
            return .default
        }
        do {
            return try JSONDecoder().decode(AppConfig.self, from: data)
        } catch {
            // Preserve the unreadable original before defaulting — the next debounced save
            // would otherwise silently destroy the user's profiles.
            let backup = url.appendingPathExtension("bad")
            do {
                if fm.fileExists(atPath: backup.path) { try fm.removeItem(at: backup) }
                try fm.copyItem(at: url, to: backup)
                Log.app.error(
                    "config.json could not be decoded (\(error)); backed it up to config.json.bad "
                        + "and reset to defaults")
            } catch let backupError {
                Log.app.error(
                    "config.json could not be decoded (\(error)) and the backup failed (\(backupError)); "
                        + "reset to defaults — the next save overwrites the unreadable file")
            }
            return .default
        }
    }

    /// Debounced write so rapid UI edits don't thrash the disk.
    private func scheduleSave() {
        saveWorkItem?.cancel()
        let snapshot = config
        let url = fileURL
        let work = DispatchWorkItem { Self.write(snapshot, to: url) }
        saveWorkItem = work
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 0.4, execute: work)
    }

    func saveNow() {
        saveWorkItem?.cancel()
        Self.write(config, to: fileURL)
    }

    /// Encode and atomically write; never throws (runs on a background queue), always logs.
    nonisolated private static func write(_ config: AppConfig, to url: URL) {
        do {
            let data = try JSONEncoder().encode(config)
            try data.write(to: url, options: .atomic)
        } catch {
            Log.app.error(
                "saving the config to \(url.path) failed: \(error) — changes since the last save are not persisted")
        }
    }
}
