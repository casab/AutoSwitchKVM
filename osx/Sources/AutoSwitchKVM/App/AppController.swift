import AppKit
import Combine
import Foundation
import UniformTypeIdentifiers

/// Top-level coordinator. Owns all managers, wires them together, and exposes actions
/// the UI calls. Created once and shared via the SwiftUI environment.
@MainActor
final class AppController: ObservableObject {
    static let shared = AppController()

    let store: ConfigStore
    let usb: USBMonitor
    let bt: BluetoothController
    let engine: SelectionEngine
    let learner: SourceLearner
    private let sleepWake = SleepWakeMonitor()
    private let dock = DockManager()
    private let hotKeys = HotKeyManager()

    /// Free-form log of recent actions, surfaced in Settings for the spike.
    @Published private(set) var log: [String] = []

    /// Mirrors `engine.selected` so the menu bar label (which observes this controller) updates.
    @Published private(set) var sourceActive = false

    /// Mirrors `config.paused` for the menu bar label / menu state.
    @Published private(set) var paused = false

    /// Mirrors `engine.bluetoothPowered` for the menu bar label.
    @Published private(set) var bluetoothPowered = true

    private let notifier = Notifier()
    private var cancellables = Set<AnyCancellable>()

    init() {
        let store = ConfigStore()
        let usb = USBMonitor()
        let bt = IOBluetoothController()
        self.store = store
        self.usb = usb
        self.bt = bt
        self.engine = SelectionEngine(store: store, usb: usb, bt: bt)
        self.learner = SourceLearner(usb: usb)

        engine.$selected
            .receive(on: RunLoop.main)
            .sink { [weak self] value in MainActor.assumeIsolated { self?.sourceActive = value } }
            .store(in: &cancellables)

        store.$config
            .map(\.paused)
            .removeDuplicates()
            .receive(on: RunLoop.main)
            .sink { [weak self] value in MainActor.assumeIsolated { self?.paused = value } }
            .store(in: &cancellables)

        engine.$bluetoothPowered
            .receive(on: RunLoop.main)
            .sink { [weak self] value in MainActor.assumeIsolated { self?.bluetoothPowered = value } }
            .store(in: &cancellables)
    }

    private var started = false

    func start() {
        guard !started else { return }
        started = true
        usb.start()
        sleepWake.start()
        sleepWake.onWillSleep = { [weak self] in self?.handleWillSleep() }
        sleepWake.onDidWake = { [weak self] in self?.handleDidWake() }

        // Apply persisted system-integration settings.
        LoginItem.setEnabled(store.config.launchAtLogin)
        dock.setEnabled(store.config.dockAutoHide)

        hotKeys.onAction = { [weak self] action in
            guard let self else { return }
            switch action {
            case .togglePause: self.togglePause()
            case .connectAll: self.connectAllNow()
            case .disconnectAll: self.disconnectAllNow()
            }
        }
        applyHotkeys()

        // User-facing notifications for connect/disconnect, gated by the setting.
        engine.onNotice = { [weak self] title, body in
            guard let self, self.store.config.showNotifications else { return }
            self.notifier.notify(title: title, body: body)
        }
        engine.onUnexpectedDisconnect = { [weak self] device in
            guard let self else { return }
            self.append("\(device.name) dropped unexpectedly")
            if self.store.config.notifyUnexpectedDisconnect {
                self.notifier.notify(
                    title: "Device disconnected",
                    body: "\(device.name) dropped while still selected")
            }
        }
        if store.config.showNotifications || store.config.notifyUnexpectedDisconnect {
            notifier.requestAuthorizationIfNeeded()
        }

        engine.seed()
        engine.startStatusMonitor()
        append("Started. Source: \(store.config.source?.name ?? "none")")
    }

    private func handleWillSleep() {
        append("System sleep → disconnecting")
        // Engine treats source-absence as the trigger; force a disconnect of enabled devices.
        Task { await self.manualDisconnectAll() }
    }

    private func handleDidWake() {
        append("Wake → re-seeding")
        usb.refreshAttached()
        engine.seed()
    }

    // MARK: - Settings application

    func setLaunchAtLogin(_ on: Bool) {
        store.config.launchAtLogin = on
        LoginItem.setEnabled(on)
    }

    func setDockAutoHide(_ on: Bool) {
        store.config.dockAutoHide = on
        dock.setEnabled(on)
    }

    func setShowNotifications(_ on: Bool) {
        store.config.showNotifications = on
        if on { notifier.requestAuthorizationIfNeeded() }
    }

    func setNotifyUnexpectedDisconnect(_ on: Bool) {
        store.config.notifyUnexpectedDisconnect = on
        if on { notifier.requestAuthorizationIfNeeded() }
    }

    func setGlobalHotkeysEnabled(_ on: Bool) {
        store.config.globalHotkeysEnabled = on
        applyHotkeys()
        append(on ? "Global shortcuts enabled" : "Global shortcuts disabled")
    }

    func setHotkey(_ action: HotKeyManager.Action, _ shortcut: KeyShortcut?) {
        switch action {
        case .togglePause: store.config.hotkeyPause = shortcut
        case .connectAll: store.config.hotkeyConnectAll = shortcut
        case .disconnectAll: store.config.hotkeyDisconnectAll = shortcut
        }
        applyHotkeys()
    }

    func resetHotkeysToDefault() {
        store.config.hotkeyPause = .defaultPause
        store.config.hotkeyConnectAll = .defaultConnectAll
        store.config.hotkeyDisconnectAll = .defaultDisconnectAll
        applyHotkeys()
        append("Global shortcuts reset to defaults")
    }

    private func applyHotkeys() {
        hotKeys.apply(
            enabled: store.config.globalHotkeysEnabled,
            pause: store.config.hotkeyPause,
            connectAll: store.config.hotkeyConnectAll,
            disconnectAll: store.config.hotkeyDisconnectAll)
    }

    // MARK: - Pause & quick actions (menu)

    func togglePause() { setPaused(!store.config.paused) }

    func setPaused(_ on: Bool) {
        store.config.paused = on
        append(on ? "Automation paused" : "Automation resumed")
        if !on { engine.reevaluate() }  // catch up to current state on resume
    }

    func connectAllNow() {
        append("Manual: connect all")
        engine.connectAllNow()
    }

    func disconnectAllNow() {
        append("Manual: disconnect all")
        engine.disconnectAllNow()
    }

    // MARK: - Profiles

    func switchProfile(to id: UUID) {
        guard store.config.activeProfileID != id,
            store.config.profiles.contains(where: { $0.id == id })
        else { return }
        store.config.activeProfileID = id
        append("Switched to profile “\(store.config.activeProfileName)”")
        reapplyActiveProfile()
    }

    func addProfile() {
        let profile = Profile(name: uniqueProfileName("Profile"))
        store.config.profiles.append(profile)
        switchProfile(to: profile.id)
    }

    func deleteActiveProfile() {
        guard store.config.profiles.count > 1 else { return }
        let removed = store.config.activeProfileID
        store.config.profiles.removeAll { $0.id == removed }
        store.config.activeProfileID = store.config.profiles[0].id
        append("Deleted profile; now on “\(store.config.activeProfileName)”")
        reapplyActiveProfile()
    }

    /// Re-point Bluetooth monitoring + presence at the (new) active profile's source/devices.
    private func reapplyActiveProfile() {
        engine.refreshMonitoring()
        engine.reevaluate()
    }

    private func uniqueProfileName(_ base: String) -> String {
        let names = Set(store.config.profiles.map(\.name))
        if !names.contains(base) { return base }
        var n = 2
        while names.contains("\(base) \(n)") { n += 1 }
        return "\(base) \(n)"
    }

    // MARK: - Debug logs

    func copyDebugLogs() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(DebugLog.shared.plainText(), forType: .string)
        append("Copied debug logs to clipboard")
    }

    func exportDebugLogs() {
        NSApp.activate(ignoringOtherApps: true)
        let panel = NSSavePanel()
        panel.title = "Export debug logs"
        panel.nameFieldStringValue = "AutoSwitchKVM-log.txt"
        panel.allowedContentTypes = [.plainText]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        try? Data(DebugLog.shared.plainText().utf8).write(to: url, options: .atomic)
    }

    // MARK: - Config import / export

    func exportConfig() {
        NSApp.activate(ignoringOtherApps: true)
        let panel = NSSavePanel()
        panel.title = "Export AutoSwitch KVM settings"
        panel.nameFieldStringValue = "AutoSwitchKVM-config.json"
        panel.allowedContentTypes = [.json]
        panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(store.config).write(to: url, options: .atomic)
            append("Exported settings to \(url.lastPathComponent)")
        } catch {
            append("Export failed: \(error)")
        }
    }

    func importConfig() {
        NSApp.activate(ignoringOtherApps: true)
        let panel = NSOpenPanel()
        panel.title = "Import AutoSwitch KVM settings"
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let data = try Data(contentsOf: url)
            let imported = try JSONDecoder().decode(AppConfig.self, from: data)
            store.config = imported
            // Re-apply system integration and reconcile to the new config.
            LoginItem.setEnabled(imported.launchAtLogin)
            dock.setEnabled(imported.dockAutoHide)
            engine.refreshMonitoring()
            engine.reevaluate()
            append("Imported settings from \(url.lastPathComponent)")
        } catch {
            append("Import failed: \(error)")
        }
    }

    // MARK: - Manual test actions (Phase 0 spike)

    func testConnect(_ device: BTDevice) {
        Task {
            append("connect \(device.name)…")
            do { try await bt.connect(device.normalizedAddress); append("connect \(device.name): ok") } catch {
                append("connect \(device.name): \(error)")
            }
        }
    }

    func testDisconnect(_ device: BTDevice) {
        Task {
            append("disconnect \(device.name)…")
            do { try await bt.disconnect(device.normalizedAddress); append("disconnect \(device.name): ok") } catch {
                append("disconnect \(device.name): \(error)")
            }
        }
    }

    func testPair(_ device: BTDevice) {
        Task {
            append("pair \(device.name)…")
            do { try await bt.pair(device.normalizedAddress); append("pair \(device.name): ok") } catch {
                append("pair \(device.name): \(error)")
            }
        }
    }

    func testUnpair(_ device: BTDevice) {
        Task {
            append("unpair \(device.name)…")
            do { try await bt.unpair(device.normalizedAddress); append("unpair \(device.name): ok") } catch {
                append("unpair \(device.name): \(error)")
            }
        }
    }

    private func manualDisconnectAll() async {
        for device in store.config.devices where device.enabled {
            try? await bt.disconnect(device.normalizedAddress)
            if device.managePairing { try? await bt.unpair(device.normalizedAddress) }
        }
    }

    // MARK: - Logging

    func append(_ line: String) {
        let ts = DateFormatter.localizedString(from: Date(), dateStyle: .none, timeStyle: .medium)
        log.insert("[\(ts)] \(line)", at: 0)
        if log.count > 200 { log.removeLast(log.count - 200) }
        Log.app.log(line)
    }
}
