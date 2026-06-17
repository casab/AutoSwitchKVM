import Combine
import Foundation

/// Loads and persists `AppConfig` as JSON in Application Support, and publishes changes.
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
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        self.fileURL = dir.appendingPathComponent("config.json")

        if let data = try? Data(contentsOf: fileURL),
            let loaded = try? JSONDecoder().decode(AppConfig.self, from: data)
        {
            self.config = loaded
        } else {
            self.config = .default
        }
    }

    /// Debounced write so rapid UI edits don't thrash the disk.
    private func scheduleSave() {
        saveWorkItem?.cancel()
        let snapshot = config
        let url = fileURL
        let work = DispatchWorkItem {
            guard let data = try? JSONEncoder().encode(snapshot) else { return }
            try? data.write(to: url, options: .atomic)
        }
        saveWorkItem = work
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 0.4, execute: work)
    }

    func saveNow() {
        saveWorkItem?.cancel()
        guard let data = try? JSONEncoder().encode(config) else { return }
        try? data.write(to: fileURL, options: .atomic)
    }
}
