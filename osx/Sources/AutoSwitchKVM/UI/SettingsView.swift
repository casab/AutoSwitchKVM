import AppKit
import SwiftUI

struct SettingsView: View {
    var body: some View {
        VStack(spacing: 0) {
            ProfilesBar()
            Divider()
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
                            Text(source.displayVidPid).font(.caption).foregroundStyle(.secondary)
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
                }
                .labelsHidden().frame(width: 92)
            }

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
    }

    private var tint: Color { device.enabled ? .accentColor : .gray }
}

struct AddDeviceSheet: View {
    @EnvironmentObject var store: ConfigStore
    @EnvironmentObject var controller: AppController
    @Environment(\.dismiss) private var dismiss

    @State private var name = ""
    @State private var address = ""
    @State private var paired: [PairedDeviceInfo] = []

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
                        }
                        Spacer()
                        Button("Use") {
                            name = p.name; address = p.address
                        }
                    }
                }
                .frame(height: 120)
                Divider()
            }

            Text("Or enter manually").font(.subheadline)
            TextField("Name", text: $name)
            TextField("Address (e.g. 3c-50-02-bf-22-45)", text: $address)

            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Add") {
                    let d = BTDevice(name: name.isEmpty ? address : name, address: address)
                    store.config.devices.append(d)
                    dismiss()
                }
                .disabled(address.trimmingCharacters(in: .whitespaces).isEmpty)
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding()
        .frame(width: 380)
        .task { paired = await controller.bt.pairedDevices() }
    }
}

// MARK: - General

struct GeneralTab: View {
    @EnvironmentObject var store: ConfigStore
    @EnvironmentObject var controller: AppController

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
