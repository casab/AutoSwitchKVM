import Foundation
import AppKit

/// Extras feature: hide the Dock when only the built-in display is present, show it when an
/// external display connects. Ports the prototype's `updateDock` via an NSScreen change watcher.
@MainActor
final class DockManager {
    private var observer: NSObjectProtocol?
    private(set) var enabled = false

    func setEnabled(_ on: Bool) {
        enabled = on
        if on {
            if observer == nil {
                observer = NotificationCenter.default.addObserver(
                    forName: NSApplication.didChangeScreenParametersNotification,
                    object: nil, queue: .main) { _ in
                        MainActor.assumeIsolated { Self.update() }
                    }
            }
            Self.update()
        } else {
            if let o = observer { NotificationCenter.default.removeObserver(o); observer = nil }
            // Restore Dock to visible when feature is turned off.
            Self.setAutohide(false)
        }
    }

    private static func update() {
        let external = NSScreen.screens.count > 1
        setAutohide(!external)   // external present -> show; built-in only -> hide
    }

    private static func setAutohide(_ flag: Bool) {
        run("/usr/bin/defaults", ["write", "com.apple.dock", "autohide", "-bool", flag ? "true" : "false"])
        run("/usr/bin/killall", ["Dock"])
    }

    private static func run(_ path: String, _ args: [String]) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = args
        try? p.run()
    }
}
