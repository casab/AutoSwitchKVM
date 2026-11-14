import AppKit
import SwiftUI

struct SettingsView: View {
    var body: some View {
        VStack(spacing: 0) {
            ProfilesBar()
            Divider()
            ErrorBanner()
            TabView {
                SourceTab().tabItem { Label("Source", systemImage: "cable.connector") }
                DevicesTab().tabItem { Label("Devices", systemImage: "dot.radiowaves.left.and.right") }
                GeneralTab().tabItem { Label("General", systemImage: "gearshape") }
                ExtrasTab().tabItem { Label("Extras", systemImage: "wand.and.stars") }
                DiagnosticsTab().tabItem { Label("Diagnostics", systemImage: "waveform.path.ecg") }
            }
            .padding()
        }
    }
}

private struct ErrorBanner: View {
    @EnvironmentObject var engine: SelectionEngine
    @State private var dismissedMessage: String?

    private var message: String? {
        for status in engine.statuses.values {
            if case .error(let message) = status { return message }
        }
        return nil
    }

    var body: some View {
        if let message, dismissedMessage != message {
            HStack(spacing: 10) {
                Label {
                    Text("Bluetooth error: \(message)")
                        .lineLimit(2)
                } icon: {
                    Image(systemName: "exclamationmark.triangle.fill")
                }
                Spacer()
                Button {
                    dismissedMessage = message
                } label: {
                    Image(systemName: "xmark.circle.fill")
                }
                .buttonStyle(.borderless)
                .help("Dismiss")
            }
            .font(.system(size: 12, weight: .medium))
            .foregroundStyle(.red)
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
            .background(Color.red.opacity(0.10))
        }
    }
}

/// App-wide profile selector: switch / add / rename / delete. Profiles scope the source + devices;
/// global options (timing, notifications, etc.) are shared across all profiles.
struct ProfilesBar: View {
    @EnvironmentObject var store: ConfigStore
    @EnvironmentObject var controller: AppController
    @State private var showRename = false

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "person.crop.rectangle.stack").foregroundStyle(.secondary)
            Text("Profile").foregroundStyle(.secondary)
            Picker(
                "Profile",
                selection: Binding(
                    get: { store.config.activeProfileID },
                    set: { controller.switchProfile(to: $0) }
                )
            ) {
                ForEach(store.config.profiles) { Text($0.name).tag($0.id) }
            }
            .labelsHidden()
            .frame(maxWidth: 220)

            Button {
                showRename = true
            } label: {
                Image(systemName: "pencil")
            }
            .help("Rename profile")
            Button {
                controller.addProfile()
            } label: {
                Image(systemName: "plus")
            }
            .help("Add profile")
            Button(role: .destructive) {
                controller.deleteActiveProfile()
            } label: {
                Image(systemName: "trash")
            }
            .help("Delete profile")
            .disabled(store.config.profiles.count <= 1)
            Spacer()
        }
        .padding(.horizontal, 14).padding(.vertical, 8)
        .sheet(isPresented: $showRename) { RenameProfileSheet() }
    }
}

struct RenameProfileSheet: View {
    @EnvironmentObject var store: ConfigStore
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Rename profile").font(.headline)
            TextField("Name", text: $name)
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Save") {
                    let trimmed = name.trimmingCharacters(in: .whitespaces)
                    if !trimmed.isEmpty { store.config.activeProfileName = trimmed }
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding()
        .frame(width: 320)
        .onAppear { name = store.config.activeProfileName }
    }
}

// MARK: - Source

struct SourceTab: View {
    @EnvironmentObject var store: ConfigStore
    @EnvironmentObject var usb: USBMonitor
    @EnvironmentObject var engine: SelectionEngine
    @EnvironmentObject var learner: SourceLearner

    private enum SourceSheet: Int, Identifiable { case learn, manual; var id: Int { rawValue } }
    @State private var sheet: SourceSheet?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Switcher source").font(.headline)
            Text(
                "The USB device(s) that appear when your KVM selects this Mac. Use Learn to detect "
                    + "them automatically by switching the KVM, or pick them manually. You can name the source."
            )
            .font(.caption).foregroundStyle(.secondary)

            if let source = store.config.source {
                GroupBox {
                    HStack(alignment: .top) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(source.name).bold()
                            Text("\(source.displayVidPid)  \(source.displayLocations)")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Text(engine.selected ? "● active" : "○ inactive")
                            .font(.caption)
                            .foregroundStyle(engine.selected ? .green : .secondary)
                    }
                }
            } else {
                Text("No source configured yet.").foregroundStyle(.secondary)
            }

            HStack {
                Button {
                    usb.refreshAttached(); learner.start(); sheet = .learn
                } label: {
                    Label("Learn source…", systemImage: "wand.and.stars")
                }
                Button {
                    usb.refreshAttached(); sheet = .manual
                } label: {
                    Label(
                        store.config.source == nil ? "Pick manually…" : "Edit…",
                        systemImage: "slider.horizontal.3")
                }
                if store.config.source != nil {
                    Button(role: .destructive) {
                        store.config.source = nil; engine.reevaluate()
                    } label: {
                        Label("Clear", systemImage: "trash")
                    }
                }
                Spacer()
            }
            Spacer()
        }
        .padding(.top, 4)
        .onAppear { usb.refreshAttached() }
        .sheet(item: $sheet) { which in
            switch which {
            case .learn:
                LearnSourceSheet(existingName: store.config.source?.name ?? "") { source in
                    store.config.source = source
                    engine.reevaluate()
                }
                .environmentObject(learner)
            case .manual:
                ManualSourceSheet(candidates: usb.attachedDevices, existing: store.config.source) { source in
                    store.config.source = source
                    engine.reevaluate()
                }
            }
        }
    }
}

// MARK: - Devices

struct DevicesTab: View {
    @EnvironmentObject var store: ConfigStore
    @EnvironmentObject var engine: SelectionEngine
    @EnvironmentObject var controller: AppController

    @State private var showAdd = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Label("Bluetooth devices", systemImage: "dot.radiowaves.left.and.right")
                    .font(.headline)
                Spacer()
                Button {
                    showAdd = true
                } label: {
                    Label("Add", systemImage: "plus")
                }
            }

            deviceArea

            Spacer(minLength: 16)
            Divider()
            LogView()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .sheet(isPresented: $showAdd) { AddDeviceSheet() }
        .task { controller.refreshChannelInfo() }
    }

    @ViewBuilder
    private var deviceArea: some View {
        if store.config.devices.isEmpty {
            VStack(spacing: 6) {
                Image(systemName: "dot.radiowaves.left.and.right")
                    .font(.system(size: 26)).foregroundStyle(.tertiary)
                Text("No devices yet").font(.system(size: 13, weight: .medium))
                Text("Add a Bluetooth device to manage automatic handoff.")
                    .font(.system(size: 12)).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 28)
        } else if store.config.devices.count > 6 {
            // Cap + scroll only when the list is long; otherwise let the cards hug the header.
            ScrollView { deviceList }.frame(height: 360)
        } else {
            deviceList
        }
    }

    private var deviceList: some View {
        VStack(spacing: 10) {
            ForEach($store.config.devices) { $device in
                let idx = store.config.devices.firstIndex { $0.id == device.id } ?? 0
                SettingsDeviceCard(
                    device: $device,
                    canMoveUp: idx > 0,
                    canMoveDown: idx < store.config.devices.count - 1,
                    onMoveUp: { moveDevice(id: device.id, by: -1) },
                    onMoveDown: { moveDevice(id: device.id, by: 1) },
                    onDelete: { store.config.devices.removeAll { $0.id == device.id } }
                )
            }
        }
    }

    private func moveDevice(id: UUID, by offset: Int) {
        guard let i = store.config.devices.firstIndex(where: { $0.id == id }) else { return }
        let j = i + offset
        guard j >= 0, j < store.config.devices.count else { return }
        store.config.devices.swapAt(i, j)
    }
}

/// A device card in Settings — same icon + status-pill language as the menu bar, plus the
/// manage-pairing toggle, manual test buttons, and delete.
struct SettingsDeviceCard: View {
    @Binding var device: BTDevice
    @EnvironmentObject var engine: SelectionEngine
    @EnvironmentObject var controller: AppController
    var canMoveUp = false
    var canMoveDown = false
    var onMoveUp: () -> Void = {}
    var onMoveDown: () -> Void = {}
    let onDelete: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 11) {
                ZStack {
                    RoundedRectangle(cornerRadius: 9).fill(tint.opacity(0.16))
                    Image(systemName: DeviceRowCard.symbol(for: device.name))
                        .font(.system(size: 17)).foregroundStyle(tint)
                }
                .frame(width: 34, height: 34)

                VStack(alignment: .leading, spacing: 2) {
                    Text(device.name).font(.system(size: 14, weight: .medium))
                    Text(device.address).font(.system(size: 11)).foregroundStyle(.secondary)
                }
                Spacer()
                StatusPill(status: device.enabled ? (engine.statuses[device.id] ?? .idle) : nil)
                Toggle("", isOn: $device.enabled)
                    .labelsHidden().toggleStyle(.switch).controlSize(.small)
            }

            HStack {
                Toggle("Manage pairing — pair on connect, unpair on disconnect", isOn: $device.managePairing)
                    .font(.system(size: 12)).toggleStyle(.checkbox)
                Spacer()
                Text("Connect delay").font(.system(size: 11)).foregroundStyle(.secondary)
                Picker("", selection: $device.connectDelayMs) {
                    Text("None").tag(0)
                    Text("0.25 s").tag(250)
                    Text("0.5 s").tag(500)
                    Text("1 s").tag(1000)
                    Text("2 s").tag(2000)
                    Text("4 s").tag(4000)
                    Text("6 s").tag(6000)
                    Text("8 s").tag(8000)
                    Text("10 s").tag(10000)
                    Text("12 s").tag(12000)
                }
                .labelsHidden().frame(width: 92)
            }

            if let a = controller.handoffAssessment(for: device.address) {
                HandoffSummary(assessment: a, managePairing: device.managePairing)
            }

            if let ch = controller.channelInfo[device.id] { channelRow(ch) }

            HStack(spacing: 8) {
                Button("Connect") { controller.testConnect(device) }
                Button("Disconnect") { controller.testDisconnect(device) }
                Button("Pair") { controller.testPair(device) }
                Button("Unpair") { controller.testUnpair(device) }
                Spacer()
                Button {
                    onMoveUp()
                } label: {
                    Image(systemName: "chevron.up")
                }
                .disabled(!canMoveUp).help("Move up (connects earlier)")
                Button {
                    onMoveDown()
                } label: {
                    Image(systemName: "chevron.down")
                }
                .disabled(!canMoveDown).help("Move down (connects later)")
                Button(role: .destructive) {
                    onDelete()
                } label: {
                    Image(systemName: "trash")
                }
                .help("Remove device")
            }
            .controlSize(.small)
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .controlBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(.quaternary, lineWidth: 0.5))
        .opacity(device.enabled ? 1 : 0.7)
        .task(id: device.address) { await controller.classify(address: device.address, name: device.name) }
    }

    /// Spec §5a type 5: the device switches channel on request (Logitech Easy-Switch, or a ZMK
    /// keyboard with the AutoSwitch module, whose channels are Bluetooth profiles). On release it's
    /// sent to the other computer's channel instead of being disconnected.
    @ViewBuilder
    private func channelRow(_ ch: HostChannels) -> some View {
        let names = controller.channelNames[device.id]
        let noun = ch.noun.lowercased()
        let label = { (n: Int) in HostChannels.label(n, names: names, noun: ch.noun) }
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Label("This Mac is on \(noun) \(ch.current + 1) of \(ch.count)", systemImage: "arrow.left.arrow.right")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                Text("Other computer's \(noun)").font(.system(size: 11)).foregroundStyle(.secondary)
                Picker("", selection: $device.releaseToChannel) {
                    Text(ch.automaticTarget.map { "Automatic (\(label($0)))" } ?? "Automatic (not found)")
                        .tag(Int?.none)
                    ForEach(1...max(1, ch.count), id: \.self) { n in
                        Text(
                            label(n - 1)
                                + (ch.isUnpaired(n - 1) ? " (not paired)" : n - 1 == ch.current ? " (this Mac)" : "")
                        )
                        .tag(Int?.some(n))
                    }
                }
                .labelsHidden().frame(width: 260)
                .help(
                    "Where the device goes when the KVM leaves this Mac. Automatic uses the \(noun) the other "
                        + "computer's AutoSwitch marked"
                        + (ch.paired == nil ? "." : ", or the only other paired one."))
            }
            // With Manage pairing on, HandoffSummary already says to turn it off.
            if device.managePairing {
            } else if device.releaseToChannel == ch.current + 1 {
                Text("That's this Mac's \(noun); pick the other computer's.")
                    .font(.system(size: 11)).foregroundStyle(.orange)
            } else if let n = device.releaseToChannel, ch.isUnpaired(n - 1) {
                Text("\(ch.noun) \(n) isn't paired with anything; the keyboard won't be sent there.")
                    .font(.system(size: 11)).foregroundStyle(.orange)
            } else if device.releaseToChannel == nil, ch.automaticTarget == nil {
                Text("Run AutoSwitch on the other computer, or pick its \(noun) here.")
                    .font(.system(size: 11)).foregroundStyle(.orange)
            }
        }
    }

    private var tint: Color { device.enabled ? .accentColor : .gray }
}

struct AddDeviceSheet: View {
    @EnvironmentObject var store: ConfigStore
    @EnvironmentObject var controller: AppController
    @Environment(\.dismiss) private var dismiss

    @State private var name = ""
    @State private var address = ""
    @State private var managePairing = false
    @State private var paired: [PairedDeviceInfo] = []

    /// 12 hex digits, separators ignored; nil until the address is complete.
    private var completeAddress: String? {
        let digits = DeviceIdentityReader.normalize(address.trimmingCharacters(in: .whitespaces))
        return digits.count == 12 && digits.allSatisfy(\.isHexDigit) ? digits : nil
    }

    private var assessment: HandoffAssessment? {
        completeAddress.flatMap { _ in controller.handoffAssessment(for: address) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Add Bluetooth device").font(.headline)

            if !paired.isEmpty {
                Text("Paired devices").font(.subheadline)
                List(paired) { p in
                    HStack {
                        VStack(alignment: .leading) {
                            Text(p.name)
                            Text(p.address).font(.caption).foregroundStyle(.secondary)
                            if let a = controller.handoffAssessment(for: p.address) {
                                Text(HandoffText.title(a.type) + HandoffText.confidence(a))
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        Spacer()
                        Button("Use") {
                            name = p.name; address = p.address
                        }
                    }
                }
                .frame(height: 150)
                Divider()
            }

            Text("Or enter manually").font(.subheadline)
            TextField("Name", text: $name)
            TextField("Address (e.g. 3c-50-02-bf-22-45)", text: $address)

            if let a = assessment {
                HandoffSummary(assessment: a)
            }
            Toggle("Manage pairing — pair on connect, unpair on disconnect", isOn: $managePairing)
                .font(.system(size: 12)).toggleStyle(.checkbox)

            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Add") {
                    let d = BTDevice(
                        name: name.isEmpty ? address : name, address: address, managePairing: managePairing)
                    store.config.devices.append(d)
                    dismiss()
                }
                .disabled(address.trimmingCharacters(in: .whitespaces).isEmpty)
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding()
        .frame(width: 420)
        .task {
            controller.identities.invalidate()  // the user may have just paired it
            paired = await controller.bt.pairedDevices()
            for p in paired { await controller.classify(address: p.address, name: p.name) }
        }
        .task(id: completeAddress) {
            // The type sets the Manage pairing default (spec §5a); the user can still change it.
            guard completeAddress != nil else { return }
            let a = await controller.classify(address: address, name: name)
            managePairing = a.defaultManagePairing(isLowEnergy: false)
        }
    }
}

/// The device's handoff type (spec §5a): what it is, what AutoSwitch does with it on this Mac,
/// and a hint when Manage pairing works against it.
struct HandoffSummary: View {
    let assessment: HandoffAssessment
    var managePairing: Bool? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            Label(
                HandoffText.title(assessment.type) + HandoffText.confidence(assessment),
                systemImage: HandoffText.symbol(assessment.type)
            )
            .font(.system(size: 12, weight: .medium))
            .foregroundStyle(HandoffText.canSwitch(assessment.type) ? Color.green : Color.secondary)
            Text(HandoffText.whatHappens(assessment.type))
                .font(.system(size: 11)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            if let managePairing, let hint = HandoffText.managePairingHint(assessment, managePairing: managePairing) {
                Text(hint).font(.system(size: 11)).foregroundStyle(.orange)
            }
            Link("About device types", destination: HandoffText.learnMoreURL).font(.system(size: 11))
        }
        .help(assessment.reason)
    }
}

// MARK: - General

struct GeneralTab: View {
    @EnvironmentObject var store: ConfigStore
    @EnvironmentObject var controller: AppController
    @ObservedObject private var updater = AppUpdater.shared

    var body: some View {
        Form {
            Section {
                TimingSlider(
                    title: "Debounce (arrival)", value: $store.config.arrivalDebounceMs,
                    range: 0...3000, step: 100, unit: "ms")
                TimingSlider(
                    title: "Debounce (departure)", value: $store.config.debounceMs,
                    range: 200...5000, step: 100, unit: "ms")
                TimingSlider(
                    title: "Connect retries", value: $store.config.connectRetryMax,
                    range: 1...20)
                TimingSlider(
                    title: "Retry interval", value: $store.config.connectRetrySecs,
                    range: 1...30, unit: "s")
                TimingSlider(
                    title: "Per-call timeout", value: $store.config.btCallTimeoutSecs,
                    range: 1...30, unit: "s")
                TimingSlider(
                    title: "Pause after unpair before re-pairing", value: $store.config.postUnpairSettleMs,
                    range: 0...5000, step: 100, unit: "ms"
                )
                .help("Only quick KVM bounces wait for this. macOS finishes removing a pairing ~1.5 s late.")
            } header: {
                Label("Timing", systemImage: "timer")
            }
            Section {
                Toggle(
                    "Pause automation",
                    isOn: Binding(
                        get: { store.config.paused },
                        set: { controller.setPaused($0) }))
                Toggle(
                    "Show notifications",
                    isOn: Binding(
                        get: { store.config.showNotifications },
                        set: { controller.setShowNotifications($0) }))
                Toggle(
                    "Notify when a device drops unexpectedly",
                    isOn: Binding(
                        get: { store.config.notifyUnexpectedDisconnect },
                        set: { controller.setNotifyUnexpectedDisconnect($0) }))
                Toggle(
                    "Auto-reconnect when a device drops unexpectedly",
                    isOn: Binding(
                        get: { store.config.autoReconnectOnDrop },
                        set: { store.config.autoReconnectOnDrop = $0 }))
                Toggle(
                    "Launch at login",
                    isOn: Binding(
                        get: { store.config.launchAtLogin },
                        set: { controller.setLaunchAtLogin($0) }))
            } header: {
                Label("Behavior", systemImage: "switch.2")
            }
            Section {
                Toggle(
                    "Enable global keyboard shortcuts",
                    isOn: Binding(
                        get: { store.config.globalHotkeysEnabled },
                        set: { controller.setGlobalHotkeysEnabled($0) }))

                Group {
                    ShortcutRecorder(label: "Pause / resume", shortcut: store.config.hotkeyPause) {
                        controller.setHotkey(.togglePause, $0)
                    }
                    ShortcutRecorder(label: "Connect all", shortcut: store.config.hotkeyConnectAll) {
                        controller.setHotkey(.connectAll, $0)
                    }
                    ShortcutRecorder(label: "Disconnect all", shortcut: store.config.hotkeyDisconnectAll) {
                        controller.setHotkey(.disconnectAll, $0)
                    }
                }
                .disabled(!store.config.globalHotkeysEnabled)

                HStack {
                    Text(
                        "System-wide while the app is running. Click Record and press a combo with "
                            + "at least one of ⌃⌥⌘⇧."
                    )
                    .font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    Button("Restore defaults") { controller.resetHotkeysToDefault() }
                        .controlSize(.small)
                }
            } header: {
                Label("Global shortcuts", systemImage: "command")
            }
            Section {
                HStack {
                    Button("Export…") { controller.exportConfig() }
                    Button("Import…") { controller.importConfig() }
                }
                Text("Back up or restore all settings (source, devices, timing, options) as a JSON file.")
                    .font(.caption).foregroundStyle(.secondary)
            } header: {
                Label("Configuration", systemImage: "square.and.arrow.up.on.square")
            }
            Section {
                LabeledContent("Version", value: AppUpdater.versionDescription)
                if updater.isAvailable {
                    Toggle(
                        "Check for updates automatically",
                        isOn: Binding(get: { updater.automaticallyChecks }, set: { updater.automaticallyChecks = $0 }))
                    Button("Check for Updates…") { updater.checkForUpdates() }
                        .disabled(!updater.canCheckForUpdates)
                } else {
                    Text("Automatic updates are available in the released app (from GitHub Releases).")
                        .font(.caption).foregroundStyle(.secondary)
                }
            } header: {
                Label("Updates", systemImage: "arrow.down.circle")
            }
        }
        .formStyle(.grouped)
    }
}

/// A labeled slider with a value readout, bound to an Int config field.
struct TimingSlider: View {
    let title: String
    @Binding var value: Int
    let range: ClosedRange<Double>
    var step: Double = 1
    var unit: String = ""

    var body: some View {
        HStack(spacing: 12) {
            Text(title).frame(width: 150, alignment: .leading)
            Slider(
                value: Binding(
                    get: { Double(value) },
                    set: { value = Int($0.rounded()) }),
                in: range, step: step)
            Text(unit.isEmpty ? "\(value)" : "\(value) \(unit)")
                .monospacedDigit()
                .foregroundStyle(.secondary)
                .frame(width: 70, alignment: .trailing)
        }
    }
}

// MARK: - Extras

struct ExtrasTab: View {
    @EnvironmentObject var store: ConfigStore
    @EnvironmentObject var controller: AppController

    var body: some View {
        Form {
            Section {
                Toggle(
                    "Auto-hide Dock when on built-in display only",
                    isOn: Binding(
                        get: { store.config.dockAutoHide },
                        set: { controller.setDockAutoHide($0) }))
                Text(
                    "Hides the Dock when no external display is connected, and shows it when one is. "
                        + "Unrelated to Bluetooth handoff."
                )
                .font(.caption).foregroundStyle(.secondary)
            } header: {
                Label("Dock", systemImage: "dock.rectangle")
            }
        }
        .formStyle(.grouped)
    }
}

/// Records a global shortcut: click to capture the next modified key-down via a local event monitor.
struct ShortcutRecorder: View {
    let label: String
    let shortcut: KeyShortcut?
    let onChange: (KeyShortcut?) -> Void

    @StateObject private var recorder = ShortcutRecorderModel()

    var body: some View {
        HStack {
            Text(label)
            Spacer()
            Button(recorder.recording ? "Press keys…" : (shortcut?.display ?? "Record")) {
                if recorder.recording { recorder.stop() } else { recorder.start(onCapture: onChange) }
            }
            .frame(minWidth: 110)
            if shortcut != nil {
                Button {
                    onChange(nil)
                } label: {
                    Image(systemName: "xmark.circle.fill")
                }
                .buttonStyle(.borderless).help("Clear shortcut")
            }
        }
        .onDisappear { recorder.stop() }
    }
}

@MainActor
final class ShortcutRecorderModel: ObservableObject {
    @Published private(set) var recording = false
    private var monitor: Any?
    private var onCapture: ((KeyShortcut?) -> Void)?

    func start(onCapture: @escaping (KeyShortcut?) -> Void) {
        self.onCapture = onCapture
        recording = true
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            // Read the (non-Sendable) NSEvent here on the main thread; pass only Sendable values
            // across the actor hop.
            let isEscape = event.keyCode == 53
            let shortcut = KeyShortcut(event: event)
            MainActor.assumeIsolated { self?.handleCapture(isEscape: isEscape, shortcut: shortcut) }
            return nil  // swallow keys while recording
        }
    }

    func stop() {
        recording = false
        if let monitor { NSEvent.removeMonitor(monitor); self.monitor = nil }
        onCapture = nil
    }

    private func handleCapture(isEscape: Bool, shortcut: KeyShortcut?) {
        if isEscape { stop(); return }  // Escape cancels
        if let shortcut {  // a valid modified combo was captured
            onCapture?(shortcut)
            stop()
        }
        // otherwise (no modifier): stay recording
    }
}

// MARK: - Log

struct LogView: View {
    @EnvironmentObject var controller: AppController

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Activity").font(.subheadline)
            ScrollView {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(Array(controller.log.enumerated()), id: \.offset) { _, line in
                        Text(line).font(.system(.caption, design: .monospaced))
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
            }
            .frame(height: 100)
        }
    }
}
