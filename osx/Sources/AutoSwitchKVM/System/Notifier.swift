import Foundation
import UserNotifications

/// Thin wrapper over UNUserNotificationCenter, gated to a real `.app` bundle.
///
/// `UNUserNotificationCenter.current()` requires a proper application bundle and crashes when
/// invoked from a bare SwiftPM executable. We therefore no-op unless we're running as a `.app`
/// (i.e. the XcodeGen build). In a plain `swift run`, notifications are silently skipped.
@MainActor
final class Notifier {
    private var available: Bool { Bundle.main.bundleURL.pathExtension == "app" }
    private var authorized = false

    func requestAuthorizationIfNeeded() {
        guard available else { return }
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { granted, _ in
            Task { @MainActor in self.authorized = granted }
        }
    }

    func notify(title: String, body: String) {
        guard available, authorized else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request)
    }
}
