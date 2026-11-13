import AppKit
import Combine
import Foundation

#if canImport(Sparkle)
    import Sparkle
#endif

/// Automatic updates via Sparkle 2, from the appcast attached to the latest GitHub release
/// (`SUFeedURL` in Info.plist; the `macos` job of `.github/workflows/release.yml` publishes it).
/// Every check's outcome is logged with both build numbers: Sparkle compares `CFBundleVersion`
/// with `sparkle:version`, and v0.3.0 shipped a lower build than the installed copies.
///
/// Sparkle is linked only into the XcodeGen `.app` target (project.yml). The SwiftPM build, used
/// by the tests and CI, doesn't have it, and there the updater is simply unavailable. It also
/// stays off when the bundle has no `SUPublicEDKey` (updates are verified with that key) or isn't
/// an `.app`.
@MainActor
final class AppUpdater: NSObject, ObservableObject {
    static let shared = AppUpdater()

    /// True once the updater is running (Sparkle linked, `.app` bundle, feed + key configured).
    @Published private(set) var isAvailable = false
    /// Mirrors Sparkle's `canCheckForUpdates` (false while a check is already running).
    @Published private(set) var canCheckForUpdates = false

    /// The version shown in Settings and the log: "1.2.0 (42)".
    nonisolated static var versionDescription: String {
        let info = Bundle.main.infoDictionary ?? [:]
        let version = info["CFBundleShortVersionString"] as? String ?? "?"
        let build = info["CFBundleVersion"] as? String ?? "?"
        return "\(version) (\(build))"
    }

    #if canImport(Sparkle)
        private var controller: SPUStandardUpdaterController?
        private var observation: NSKeyValueObservation?
    #endif

    func start() {
        #if canImport(Sparkle)
            guard controller == nil else { return }
            let info = Bundle.main.infoDictionary ?? [:]
            let key = (info["SUPublicEDKey"] as? String)?.trimmingCharacters(in: .whitespaces) ?? ""
            let feed = (info["SUFeedURL"] as? String) ?? ""
            guard Bundle.main.bundleURL.pathExtension == "app" else {
                Log.app.debug("updates: not an .app bundle; the updater stays off")
                return
            }
            guard !key.isEmpty, !feed.isEmpty else {
                Log.app.warn("updates: no SUPublicEDKey/SUFeedURL in Info.plist; automatic updates are off")
                return
            }
            let controller = SPUStandardUpdaterController(
                startingUpdater: true, updaterDelegate: self, userDriverDelegate: self)
            self.controller = controller
            observation = controller.updater.observe(\.canCheckForUpdates, options: [.initial, .new]) {
                [weak self] updater, _ in
                let can = updater.canCheckForUpdates
                Task { @MainActor in self?.canCheckForUpdates = can }
            }
            isAvailable = true
            Log.app.log(
                "updates: on (\(controller.updater.automaticallyChecksForUpdates ? "automatic checks" : "manual checks only"); "
                    + "feed \(controller.updater.feedURL?.absoluteString ?? feed))")
        #else
            Log.app.debug("updates: Sparkle isn't linked into this build (SwiftPM); the updater stays off")
        #endif
    }

    /// "Check for Updates…": shows Sparkle's window with the result.
    func checkForUpdates() {
        #if canImport(Sparkle)
            guard let controller else { return }
            Log.app.log("updates: checking for updates (user)")
            NSApp.activate(ignoringOtherApps: true)
            controller.checkForUpdates(nil)
        #endif
    }

    /// Whether Sparkle checks on its own schedule (once a day by default). Stored by Sparkle in
    /// the app's defaults, not in config.json: it's a per-Mac preference of the updater.
    var automaticallyChecks: Bool {
        get {
            #if canImport(Sparkle)
                return controller?.updater.automaticallyChecksForUpdates ?? false
            #else
                return false
            #endif
        }
        set {
            #if canImport(Sparkle)
                controller?.updater.automaticallyChecksForUpdates = newValue
                objectWillChange.send()
                Log.app.log("updates: automatic checks \(newValue ? "on" : "off")")
            #endif
        }
    }
}

#if canImport(Sparkle)
    /// The outcome of every check, in our own log: Sparkle's own lines only go to the unified log.
    extension AppUpdater: SPUUpdaterDelegate {
        nonisolated private static func describe(_ item: SUAppcastItem?) -> String {
            guard let item else { return "none" }
            return "\(item.displayVersionString) (build \(item.versionString))"
        }

        nonisolated func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
            Log.app.log("updates: \(Self.describe(item)) is available (this copy: \(AppUpdater.versionDescription))")
        }

        nonisolated func updaterDidNotFindUpdate(_ updater: SPUUpdater, error: Error) {
            let info = (error as NSError).userInfo
            let latest = info[SPULatestAppcastItemFoundKey] as? SUAppcastItem
            let reason: String
            switch (info[SPUNoUpdateFoundReasonKey] as? NSNumber).flatMap({
                SPUNoUpdateFoundReason(rawValue: $0.int32Value)
            }) {
            case .onLatestVersion: reason = "up to date"
            case .onNewerThanLatestVersion: reason = "this copy is newer than the feed's"
            case .systemIsTooOld: reason = "the update needs a newer macOS"
            case .systemIsTooNew: reason = "the update doesn't support this macOS"
            default: reason = "no update"
            }
            Log.app.log(
                "updates: \(reason) (this copy: \(AppUpdater.versionDescription); newest in the feed: "
                    + "\(Self.describe(latest)); Sparkle compares build numbers)")
        }

        nonisolated func updater(_ updater: SPUUpdater, didAbortWithError error: Error) {
            let nsError = error as NSError
            // "No update" also arrives here; updaterDidNotFindUpdate already logged it.
            guard nsError.domain != SUSparkleErrorDomain || nsError.code != Int(SUError.noUpdateError.rawValue) else {
                return
            }
            Log.app.warn("updates: check failed: \(error.localizedDescription) (\(nsError.domain) \(nsError.code))")
        }

        nonisolated func updater(_ updater: SPUUpdater, failedToDownloadUpdate item: SUAppcastItem, error: Error) {
            Log.app.warn("updates: download of \(Self.describe(item)) failed: \(error.localizedDescription)")
        }

        nonisolated func updater(_ updater: SPUUpdater, willInstallUpdate item: SUAppcastItem) {
            Log.app.log("updates: installing \(Self.describe(item))")
        }
    }

    extension AppUpdater: SPUStandardUserDriverDelegate {
        /// A menu bar app has no Dock icon or main window, so Sparkle shows scheduled update
        /// alerts gently instead of popping a window over whatever the user is doing.
        nonisolated var supportsGentleScheduledUpdateReminders: Bool { true }

        nonisolated func standardUserDriverWillHandleShowingUpdate(
            _ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem, state: SPUUserUpdateState
        ) {
            // Bring the alert forward: an accessory app's windows otherwise open behind others.
            Task { @MainActor in NSApp.activate(ignoringOtherApps: true) }
        }
    }
#endif
