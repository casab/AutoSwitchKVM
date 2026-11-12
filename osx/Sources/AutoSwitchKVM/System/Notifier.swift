import Foundation
@preconcurrency import UserNotifications

/// Thin wrapper over UNUserNotificationCenter, gated to a real `.app` bundle.
///
/// `UNUserNotificationCenter.current()` requires a proper application bundle and crashes when
/// invoked from a bare SwiftPM executable. We therefore no-op unless we're running as a `.app`
/// (i.e. the XcodeGen build). In a plain `swift run`, notifications are skipped (logged once).
@MainActor
final class Notifier {
    private var available: Bool { Bundle.main.bundleURL.pathExtension == "app" }
    private var reportedUnavailable = false

    /// Log once that notifications can't be shown in this packaging, instead of skipping silently.
    private func checkAvailable() -> Bool {
        if available { return true }
        if !reportedUnavailable {
            reportedUnavailable = true
            Log.app.warn("notifications unavailable: not running from an .app bundle (SwiftPM build)")
        }
        return false
    }

    func requestAuthorizationIfNeeded() {
        guard checkAvailable() else { return }
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { granted, error in
            if let error {
                Log.app.error("notification authorization request failed: \(error)")
            } else if !granted {
                Log.app.warn("notification authorization denied — alerts will be suppressed")
            }
        }
    }

    func notify(title: String, body: String) {
        guard checkAvailable() else { return }
        // Read the live authorization state each time instead of caching the request callback's
        // answer: the cached flag raced early notifications and never reflected later changes
        // made in System Settings.
        let center = UNUserNotificationCenter.current()
        center.getNotificationSettings { settings in
            switch settings.authorizationStatus {
            case .authorized, .provisional:
                let content = UNMutableNotificationContent()
                content.title = title
                content.body = body
                center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)) {
                    error in
                    if let error { Log.app.error("posting notification '\(title)' failed: \(error)") }
                }
            default:
                Log.app.debug("notification suppressed (authorization: \(settings.authorizationStatus.rawValue))")
            }
        }
    }
}
