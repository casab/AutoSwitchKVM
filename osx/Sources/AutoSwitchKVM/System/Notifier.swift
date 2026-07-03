import Foundation
@preconcurrency import UserNotifications

/// Thin wrapper over UNUserNotificationCenter, gated to a real `.app` bundle.
///
/// `UNUserNotificationCenter.current()` requires a proper application bundle and crashes when
/// invoked from a bare SwiftPM executable. We therefore no-op unless we're running as a `.app`
/// (i.e. the XcodeGen build). In a plain `swift run`, notifications are silently skipped.
@MainActor
final class Notifier {
    private var available: Bool { Bundle.main.bundleURL.pathExtension == "app" }

    func requestAuthorizationIfNeeded() {
        guard available else { return }
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { granted, _ in
            if !granted {
                Log.app.log("notification authorization denied — alerts will be suppressed")
            }
        }
    }

    func notify(title: String, body: String) {
        guard available else { return }
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
                center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
            default:
                Log.app.log("notification suppressed (authorization: \(settings.authorizationStatus.rawValue))")
            }
        }
    }
}
