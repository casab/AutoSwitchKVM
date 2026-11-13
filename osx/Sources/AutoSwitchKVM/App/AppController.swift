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
    /// Logitech Easy-Switch channel switching (spec §5a type 5); also asked by Settings.
    let hostSwitcher: HostSwitcher
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
        // Start the persistent log first, so even config-load problems land in the file.
        // (Only the app does this — tests never construct AppController.)
        FileLog.shared.start()
        let store = ConfigStore()
        LogThreshold.shared.level = store.config.logLevel  // before anything else is logged
        let usb = USBMonitor()
        let bt = IOBluetoothController()
        self.store = store
        self.usb = usb
        self.bt = bt
        let hostSwitcher = LogitechHostSwitcher()
        self.hostSwitcher = hostSwitcher
        self.engine = SelectionEngine(store: store, usb: usb, bt: bt, hostSwitcher: hostSwitcher)
        self.learner = SourceLearner(usb: usb)

        // DispatchQueue.main, not RunLoop.main: run-loop delivery stalls while a menu/modal is
        // open, which would freeze the menu-bar icon state mid-interaction.
        engine.$selected
            .receive(on: DispatchQueue.main)
            .sink { [weak self] value in MainActor.assumeIsolated { self?.sourceActive = value } }
            .store(in: &cancellables)

        store.$config
            .map(\.paused)
            .removeDuplicates()
            .receive(on: DispatchQueue.main)
            .sink { [weak self] value in MainActor.assumeIsolated { self?.paused = value } }
            .store(in: &cancellables)

        engine.$bluetoothPowered
            .receive(on: DispatchQueue.main)
            .sink { [weak self] value in MainActor.assumeIsolated { self?.bluetoothPowered = value } }
            .store(in: &cancellables)

        // Apply a changed log level at once (Settings ▸ Diagnostics); the change itself is always
        // written, whatever the level.
        store.$config
            .map(\.logLevel)
            .removeDuplicates()
            .dropFirst()
            .sink { level in
                LogThreshold.shared.level = level
                Log.app.always("log level set to \(level.rawValue) (\(level.title))")
            }
            .store(in: &cancellables)
    }

    private var started = false

    func start() {
        guard !started else { return }
        started = true
        usb.start()
        sleepWake.start()
        sleepWake.onWillSleep = { [weak self] in await self?.handleWillSleep() }
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
            self.append("\(device.name) dropped unexpectedly", level: .debug)
            if self.store.config.notifyUnexpectedDisconnect {
                self.notifier.notify(
                    title: "Device disconnected",
                    body: "\(device.name) dropped while still selected")
            }
        }
        if store.config.showNotifications || store.config.notifyUnexpectedDisconnect {
            notifier.requestAuthorizationIfNeeded()
        }

        logStartupDiagnostics()
        AppUpdater.shared.start()
        engine.seed()
        engine.startStatusMonitor()
        append("Started. Source: \(store.config.source?.name ?? "none")", level: .debug)
    }

    /// One block at launch with everything needed to read the log that follows: build, OS,
    /// Bluetooth permission, and the settings that shape a handoff.
    private func logStartupDiagnostics() {
        let info = Bundle.main.infoDictionary ?? [:]
        let version = info["CFBundleShortVersionString"] as? String ?? "?"
        let build = info["CFBundleVersion"] as? String ?? "?"
        let packaging = Bundle.main.bundleURL.pathExtension == "app" ? ".app bundle" : "bare executable (SwiftPM)"
        Log.app.log(
            "AutoSwitch KVM \(version) (\(build)), \(packaging), macOS \(ProcessInfo.processInfo.operatingSystemVersionString)"
        )
        Log.app.log("log file: \(FileLog.shared.folderURL?.path ?? "disabled")")

        let permission = BluetoothPermission.current
        if permission.isProblem {
            Log.app.error("Bluetooth permission: \(permission.description)")
            append("Bluetooth permission: \(permission.description)", level: .debug)
        } else {
            Log.app.log("Bluetooth permission: \(permission.description)")
        }

        let c = store.config
        Log.app.log(
            "config: profile '\(c.activeProfileName)', source \(c.source.map { "'\($0.name)' \($0.displayVidPid) \($0.displayLocations)" } ?? "none"); "
                + "debounce arrival \(c.arrivalDebounceMs) ms / departure \(c.debounceMs) ms; "
                + "retries \(c.connectRetryMax) (base \(c.connectRetrySecs) s); call timeout \(c.btCallTimeoutSecs) s; "
                + "paused \(c.paused); autoReconnectOnDrop \(c.autoReconnectOnDrop); log level \(c.logLevel.rawValue)")
        for d in c.devices {
            Log.app.log(
                "device '\(d.name)' \(d.normalizedAddress): enabled \(d.enabled), managePairing \(d.managePairing), "
                    + "connectDelay \(d.connectDelayMs) ms")
        }
    }

    /// Awaited by SleepWakeMonitor, which holds the sleep transition (bounded) until this
    /// returns — so the release/unpair the other host depends on actually happens before sleep.
    /// Goes through the engine: it halts automation first, so the release's own disconnect
    /// events can't trigger an auto-reconnect during sleep.
    private func handleWillSleep() async {
        append("System sleep → releasing devices", level: .debug)
        await engine.prepareForSleep()
        append("Devices released; allowing sleep", level: .debug)
    }

    private func handleDidWake() {
        append("Wake → re-seeding", level: .debug)
        usb.refreshAttached()
        engine.resumeAfterWake()
    }

    // MARK: - Settings application

    func setLaunchAtLogin(_ on: Bool) {
        store.config.launchAtLogin = on
        if let notice = LoginItem.setEnabled(on) { append(notice) }
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
        let failures = hotKeys.apply(
            enabled: store.config.globalHotkeysEnabled,
            pause: store.config.hotkeyPause,
            connectAll: store.config.hotkeyConnectAll,
            disconnectAll: store.config.hotkeyDisconnectAll)
        for failure in failures { append("Shortcut \(failure)", level: .debug) }
    }

    // MARK: - Pause & quick actions (menu)

    func togglePause() { setPaused(!store.config.paused) }

    func setPaused(_ on: Bool) {
        store.config.paused = on
        append(on ? "Automation paused" : "Automation resumed")
        if !on { engine.reevaluate() }  // catch up to current state on resume
    }

    func connectAllNow() {
        append("Manual: connect all", level: .debug)
        engine.connectAllNow()
    }

    func disconnectAllNow() {
        append("Manual: disconnect all", level: .debug)
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
        do {
            try Data(DebugLog.shared.plainText().utf8).write(to: url, options: .atomic)
            append("Exported debug logs to \(url.lastPathComponent)")
        } catch {
            Log.app.error("exporting debug logs to \(url.path) failed: \(error)")
            append("Export failed: \(error.localizedDescription)", level: .error)
        }
    }

    /// Reveal the persistent log files (`~/Library/Logs/AutoSwitchKVM/`) in Finder.
    func revealLogFiles() {
        FileLog.shared.flush()
        if let file = FileLog.shared.fileURL {
            NSWorkspace.shared.activateFileViewerSelecting([file])
        } else if let folder = FileLog.shared.folderURL {
            NSWorkspace.shared.open(folder)
        } else {
            append("File logging is not running")
        }
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
            append("Export failed: \(error)", level: .error)
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
            applyHotkeys()
            if imported.showNotifications || imported.notifyUnexpectedDisconnect {
                notifier.requestAuthorizationIfNeeded()
            }
            engine.refreshMonitoring()
            engine.reevaluate()
            append("Imported settings from \(url.lastPathComponent)")
        } catch {
            append("Import failed: \(error)", level: .error)
        }
    }

    // MARK: - Manual per-device actions (Settings ▸ Devices test buttons)

    // These go through the engine, never `bt` directly: the engine marks the device busy,
    // supersedes in-flight automation on it, and sets the status before clearing busy — so the
    // stack's late "disconnected" event can't look like an unexpected drop and trigger an
    // auto-reconnect that undoes the user's Disconnect/Unpair.

    /// Full connect flow (pair first when managePairing, retries included), ignoring selection.
    func testConnect(_ device: BTDevice) {
        Task {
            append("connect \(device.name)…", level: .debug)
            await engine.connectDeviceNow(device)
            append("connect \(device.name): \(engine.statuses[device.id]?.label ?? "done")", level: .debug)
        }
    }

    func testDisconnect(_ device: BTDevice) {
        Task {
            append("disconnect \(device.name)…", level: .debug)
            let error = await engine.disconnectDeviceNow(device)
            append("disconnect \(device.name): \(error ?? "ok")", level: .debug)
        }
    }

    // MARK: - Channel switching info (spec §5a type 5)

    /// Live channel info of devices that switch channel on request, for Settings ▸ Devices. Kept
    /// after the device leaves (it can only answer while connected here), so the setting stays
    /// visible while the KVM is on the other computer.
    @Published private(set) var channelInfo: [UUID: HostChannels] = [:]

    /// The name each channel's computer gave itself when it paired (HID++ 0x1815), for the
    /// channel choice. Kept like `channelInfo`.
    @Published private(set) var channelNames: [UUID: [String?]] = [:]

    /// Ask each enabled device that isn't pairing-managed whether it switches channel on request.
    /// Read-only HID++ queries; a device that isn't connected here simply doesn't answer.
    func refreshChannelInfo() {
        let devices = store.config.devices.filter { $0.enabled }
        Task { @MainActor in
            for device in devices {
                do {
                    let ch = try await withTimeout(5) { try await self.hostSwitcher.channels(device.normalizedAddress) }
                    if let ch {
                        channelInfo[device.id] = ch
                        handoff[DeviceIdentityReader.normalize(device.address)] = .channelSwitch
                        engine.noteSwitchesChannels(device.id)
                        let names = try await withTimeout(5) {
                            try await self.hostSwitcher.channelNames(device.normalizedAddress)
                        }
                        if let names { channelNames[device.id] = names }
                    }
                } catch {
                    Log.app.debug("\(device.name): channel query failed: \(error)")
                }
            }
        }
    }

    // MARK: - Device handoff types (spec §5a)

    let identities = DeviceIdentityReader()

    /// Classification per normalized address (`DeviceIdentityReader.normalize`), for Settings.
    @Published private(set) var handoff: [String: HandoffAssessment] = [:]

    func handoffAssessment(for address: String) -> HandoffAssessment? {
        handoff[DeviceIdentityReader.normalize(address)]
    }

    /// Classify a device (spec §5a): the catalog rules from what macOS knows about it, then, for
    /// a likely Easy-Switch device, a live read-only Change Host query (answers only while the
    /// device is connected here). A device that answered once stays type 5.
    @discardableResult
    func classify(address: String, name: String) async -> HandoffAssessment {
        let key = DeviceIdentityReader.normalize(address)
        if handoff[key]?.type == .channelSwitch { return .channelSwitch }
        let identity = await identities.identity(address: address, name: name)
        var assessment = HandoffCatalog.classify(identity)
        if assessment.type == .devicePicksHost, identity.vendorID == HandoffCatalog.logitech {
            do {
                if try await withTimeout(3, { try await self.hostSwitcher.channels(address) }) != nil {
                    assessment = .channelSwitch
                }
            } catch {
                Log.app.debug("\(name): channel query failed: \(error)")
            }
        }
        if handoff[key] != assessment {
            let ids = [identity.vendorID, identity.productID].map { $0.map { String(format: "%04X", $0) } ?? "?" }
            Log.app.debug(
                "\(name): handoff type \(assessment.type) (\(assessment.confidence)); vendor:product \(ids[0]):\(ids[1]), "
                    + "kind \(identity.kind)")
            handoff[key] = assessment
        }
        return assessment
    }

    func testPair(_ device: BTDevice) {
        Task {
            append("pair \(device.name)…", level: .debug)
            let error = await engine.pairDeviceNow(device)
            append("pair \(device.name): \(error ?? "ok")", level: .debug)
        }
    }

    func testUnpair(_ device: BTDevice) {
        Task {
            append("unpair \(device.name)…", level: .debug)
            let error = await engine.unpairDeviceNow(device)
            append("unpair \(device.name): \(error ?? "ok")", level: .debug)
        }
    }

    // MARK: - Logging

    /// Adds a line to the in-app activity list (Settings) and logs it at `level`. Lines that
    /// repeat what the engine or a system monitor already logged pass `.debug`, so a Normal log
    /// has one line per event.
    func append(_ line: String, level: LogLevel = .info) {
        let ts = DateFormatter.localizedString(from: Date(), dateStyle: .none, timeStyle: .medium)
        log.insert("[\(ts)] \(line)", at: 0)
        if log.count > 200 { log.removeLast(log.count - 200) }
        switch level {
        case .error: Log.app.error(line)
        case .warning: Log.app.warn(line)
        case .info: Log.app.log(line)
        case .debug: Log.app.debug(line)
        }
    }
}
