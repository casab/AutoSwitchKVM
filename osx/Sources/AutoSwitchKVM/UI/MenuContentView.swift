import SwiftUI
import AppKit

/// Custom menu bar panel (rendered via `.menuBarExtraStyle(.window)`): a status header, device
/// cards with status pills + toggles, quick actions, and a footer.
struct MenuContentView: View {
    @EnvironmentObject var store: ConfigStore
    @EnvironmentObject var engine: SelectionEngine
    @EnvironmentObject var controller: AppController
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            if store.config.profiles.count > 1 {
                profileSwitcher
                Divider()
            }
            devices
            if !store.config.devices.isEmpty { quickActions }
            Divider()
            footer
        }
        .frame(width: 300)
    }

    /// Switch profiles: side-by-side segments for up to 3, an ellipsis overflow menu for more.
    /// Hidden entirely when there's only one profile (handled by the caller).
    @ViewBuilder
    private var profileSwitcher: some View {
        if store.config.profiles.count <= 3 {
            Picker("Profile", selection: profileSelection) {
                ForEach(store.config.profiles) { Text($0.name).tag($0.id) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(.horizontal, 12).padding(.vertical, 8)
        } else {
            HStack {
                Menu {
                    Picker("Profile", selection: profileSelection) {
                        ForEach(store.config.profiles) { Text($0.name).tag($0.id) }
                    }
                } label: {
                    HStack(spacing: 6) {
                        Text(store.config.activeProfileName).font(.system(size: 12, weight: .medium))
                        Image(systemName: "ellipsis.circle")
                    }
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
                Spacer()
            }
            .padding(.horizontal, 12).padding(.vertical, 8)
        }
    }

    private var profileSelection: Binding<UUID> {
        Binding(get: { store.config.activeProfileID },
                set: { controller.switchProfile(to: $0) })
    }

    // MARK: Header

    private var header: some View {
        HStack(spacing: 10) {
            Circle().fill(statusColor).frame(width: 9, height: 9)
            VStack(alignment: .leading, spacing: 1) {
                Text(statusTitle).font(.system(size: 13, weight: .semibold))
                Text(store.config.source?.name ?? "No source")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
            }
            Spacer()
            Button { controller.togglePause() } label: {
                Image(systemName: controller.paused ? "play.fill" : "pause.fill")
                    .font(.system(size: 12))
            }
            .buttonStyle(PanelButtonStyle())
            .help(controller.paused ? "Resume automation" : "Pause automation")
        }
        .padding(.horizontal, 14).padding(.vertical, 12)
    }

    private var statusColor: Color {
        if !engine.bluetoothPowered { return .red }
        if controller.paused { return .orange }
        return engine.selected ? .green : .gray
    }
    private var statusTitle: String {
        if !engine.bluetoothPowered { return "Bluetooth off" }
        if controller.paused { return "Automation paused" }
        return engine.selected ? "Source active" : "Source inactive"
    }

    // MARK: Devices

    @ViewBuilder
    private var devices: some View {
        if store.config.devices.isEmpty {
            Text("No Bluetooth devices configured")
                .font(.system(size: 12)).foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 14).padding(.vertical, 16)
        } else if store.config.devices.count > 6 {
            // Only scroll (with a fixed height) when the list is long; a ScrollView with no
            // fixed height collapses to nothing in an auto-sizing menu bar panel.
            ScrollView { deviceList }.frame(height: 340)
        } else {
            deviceList
        }
    }

    private var deviceList: some View {
        VStack(spacing: 8) {
            ForEach($store.config.devices) { $device in
                DeviceRowCard(device: $device)
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 10)
    }

    // MARK: Quick actions

    private var quickActions: some View {
        HStack(spacing: 8) {
            Button { controller.connectAllNow() } label: {
                Label("Connect all", systemImage: "link").frame(maxWidth: .infinity)
            }
            Button { controller.disconnectAllNow() } label: {
                Label("Disconnect all", systemImage: "xmark.circle").frame(maxWidth: .infinity)
            }
        }
        .controlSize(.small)
        .padding(.horizontal, 12).padding(.top, 2).padding(.bottom, 12)
    }

    // MARK: Footer

    private var footer: some View {
        HStack(spacing: 16) {
            Button {
                openWindow(id: "settings")
                NSApp.activate(ignoringOtherApps: true)
            } label: { Label("Settings", systemImage: "gearshape") }
            Spacer()
            Button { NSApp.terminate(nil) } label: { Label("Quit", systemImage: "power") }
        }
        .buttonStyle(PanelButtonStyle())
        .font(.system(size: 12))
        .foregroundStyle(.secondary)
        .padding(.horizontal, 8).padding(.vertical, 7)
    }
}

/// Menu-style button: a rounded background that highlights only this control on hover/press,
/// so feedback is localized rather than affecting the whole panel.
struct PanelButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        Content(configuration: configuration)
    }

    private struct Content: View {
        let configuration: ButtonStyle.Configuration
        @State private var hovering = false

        var body: some View {
            configuration.label
                .padding(.horizontal, 8).padding(.vertical, 5)
                .background(RoundedRectangle(cornerRadius: 6).fill(fill))
                .contentShape(RoundedRectangle(cornerRadius: 6))
                .onHover { hovering = $0 }
        }

        private var fill: Color {
            if configuration.isPressed { return .primary.opacity(0.16) }
            return hovering ? .primary.opacity(0.07) : .clear
        }
    }
}

/// A single device row: tinted type icon, name, status pill, enable toggle.
struct DeviceRowCard: View {
    @Binding var device: BTDevice
    @EnvironmentObject var engine: SelectionEngine

    var body: some View {
        HStack(spacing: 11) {
            ZStack {
                RoundedRectangle(cornerRadius: 9).fill(iconTint.opacity(0.16))
                Image(systemName: Self.symbol(for: device.name))
                    .font(.system(size: 17)).foregroundStyle(iconTint)
            }
            .frame(width: 34, height: 34)

            VStack(alignment: .leading, spacing: 2) {
                Text(device.name).font(.system(size: 13, weight: .medium))
                StatusPill(status: device.enabled ? (engine.statuses[device.id] ?? .idle) : nil)
            }

            Spacer()

            Toggle("", isOn: $device.enabled)
                .labelsHidden().toggleStyle(.switch).controlSize(.mini)
        }
        .padding(.horizontal, 10).padding(.vertical, 9)
        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(.quaternary, lineWidth: 0.5))
        .opacity(device.enabled ? 1 : 0.6)
    }

    private var iconTint: Color { device.enabled ? .accentColor : .gray }

    static func symbol(for name: String) -> String {
        let n = name.lowercased()
        if n.contains("keyboard") { return "keyboard" }
        if n.contains("mouse") { return "computermouse" }
        if n.contains("trackpad") { return "rectangle.and.hand.point.up.left" }
        if n.contains("headphone") || n.contains("airpod") || n.contains("buds") { return "headphones" }
        return "dot.radiowaves.left.and.right"
    }
}

/// Colored status pill. `nil` status means the device isn't managed (toggle off).
struct StatusPill: View {
    let status: DeviceStatus?

    var body: some View {
        if let status {
            HStack(spacing: 4) {
                Image(systemName: icon(status)).font(.system(size: 9))
                Text(label(status)).font(.system(size: 11))
            }
            .padding(.horizontal, 7).padding(.vertical, 1)
            .background(Capsule().fill(color(status).opacity(0.16)))
            .foregroundStyle(color(status))
        } else {
            Text("Not managed").font(.system(size: 11)).foregroundStyle(.tertiary)
        }
    }

    private func color(_ s: DeviceStatus) -> Color {
        switch s {
        case .connected: return .green
        case .connecting: return .orange
        case .error: return .red
        case .bluetoothOff: return .orange
        case .disconnected, .idle: return .gray
        }
    }
    private func icon(_ s: DeviceStatus) -> String {
        switch s {
        case .connected: return "checkmark.circle.fill"
        case .connecting: return "arrow.triangle.2.circlepath"
        case .error: return "exclamationmark.triangle.fill"
        case .bluetoothOff: return "exclamationmark.circle.fill"
        case .disconnected: return "minus.circle.fill"
        case .idle: return "circle"
        }
    }
    private func label(_ s: DeviceStatus) -> String {
        switch s {
        case .connected: return "Connected"
        case .connecting: return "Connecting…"
        case .error: return "Error"
        case .bluetoothOff: return "Bluetooth off"
        case .disconnected: return "Disconnected"
        case .idle: return "Idle"
        }
    }
}
