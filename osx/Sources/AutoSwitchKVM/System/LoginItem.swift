import Foundation
import ServiceManagement

/// Launch-at-login via SMAppService (macOS 13+).
enum LoginItem {
    /// Returns a user-facing notice when the request didn't take effect (registration failed,
    /// or macOS wants explicit approval in System Settings) — the toggle otherwise silently
    /// shows a state the OS never applied. Nil on success.
    @discardableResult
    static func setEnabled(_ enabled: Bool) -> String? {
        do {
            if enabled {
                if SMAppService.mainApp.status != .enabled {
                    try SMAppService.mainApp.register()
                }
                if SMAppService.mainApp.status == .requiresApproval {
                    return "Launch at login needs approval in System Settings ▸ General ▸ Login Items"
                }
                if SMAppService.mainApp.status != .enabled {
                    return "Launch at login could not be enabled (status: \(SMAppService.mainApp.status.rawValue))"
                }
            } else {
                if SMAppService.mainApp.status == .enabled {
                    try SMAppService.mainApp.unregister()
                }
            }
            return nil
        } catch {
            Log.app.error("LoginItem error: \(error)")
            return "Launch at login change failed: \(error.localizedDescription)"
        }
    }

    static var isEnabled: Bool {
        SMAppService.mainApp.status == .enabled
    }
}
