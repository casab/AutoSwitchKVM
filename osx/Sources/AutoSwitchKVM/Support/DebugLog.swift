import Foundation

/// In-app capture of the app's log lines (mirrors what goes to `os.Logger`) so they can be
/// viewed, copied, and exported from Settings ▸ Extras for troubleshooting.
@MainActor
final class DebugLog: ObservableObject {
    static let shared = DebugLog()

    struct Entry: Identifiable {
        let id = UUID()
        let date: Date
        let category: String
        let message: String
    }

    @Published private(set) var entries: [Entry] = []
    private let maxEntries = 3000

    func add(date: Date, category: String, message: String) {
        entries.append(Entry(date: date, category: category, message: message))
        if entries.count > maxEntries { entries.removeFirst(entries.count - maxEntries) }
    }

    func clear() { entries.removeAll() }

    func plainText() -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return
            entries
            .map { "[\(f.string(from: $0.date))] [\($0.category)] \($0.message)" }
            .joined(separator: "\n")
    }
}
