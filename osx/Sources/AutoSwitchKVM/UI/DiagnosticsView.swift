import AppKit
import SwiftUI

/// Read-only troubleshooting view: current engine/adapter state, live USB devices (with the
/// configured source highlighted), and paired Bluetooth devices with live connection status.
struct DiagnosticsTab: View {
    @EnvironmentObject var store: ConfigStore
    @EnvironmentObject var engine: SelectionEngine
    @EnvironmentObject var controller: AppController
    @EnvironmentObject var usb: USBMonitor
    @EnvironmentObject var debug: DebugLog

    @State private var paired: [PairedDeviceInfo] = []
    @State private var connectedAddrs: Set<String> = []
    @State private var btPowered: Bool?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                statusCard
                usbSection
                bluetoothSection
                debugSection
            }
            .padding()
        }
        .task { await refresh() }
    }

    // MARK: Debug logs

    private var debugSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Label("Debug logs", systemImage: "ladybug").font(.headline)
                Spacer()
                Button("Copy") { controller.copyDebugLogs() }
                Button("Export…") { controller.exportDebugLogs() }
                Button("Clear") { debug.clear() }
            }
            .controlSize(.small)

            if debug.entries.isEmpty {
                Text("No log entries yet.").font(.system(size: 12)).foregroundStyle(.secondary)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 1) {
                        ForEach(debug.entries.suffix(400)) { entry in
                            Text("[\(entry.category)] \(entry.message)")
                                .font(.system(.caption2, design: .monospaced))
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .textSelection(.enabled)
                        }
                    }
                }
                .frame(height: 200)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .controlBackgroundColor)))
                .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(.quaternary, lineWidth: 0.5))
            }
        }
    }

    // MARK: Status

    private var statusCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            row("Source", store.config.source?.name ?? "—")
            row("State", controller.paused ? "Paused" : (engine.selected ? "Active" : "Inactive"))
            row("Bluetooth adapter", btPowered == nil ? "Unknown" : (btPowered! ? "On" : "Off"))
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .controlBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(.quaternary, lineWidth: 0.5))
    }

    private func row(_ key: String, _ value: String) -> some View {
        HStack {
            Text(key).foregroundStyle(.secondary)
            Spacer()
            Text(value).font(.system(size: 13, weight: .medium))
        }
        .font(.system(size: 13))
    }

    // MARK: USB

    private var usbSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Label("USB devices", systemImage: "cable.connector").font(.headline)
                Spacer()
                Button {
                    usb.refreshAttached()
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .buttonStyle(.borderless)
            }
            if usb.attachedDevices.isEmpty {
                Text("None detected").font(.system(size: 12)).foregroundStyle(.secondary)
            } else {
                ForEach(usb.attachedDevices) { d in
                    HStack {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(d.name.isEmpty ? "Unknown device" : d.name).font(.system(size: 13))
                            Text(String(format: "0x%04X:0x%04X", d.vendorID, d.productID))
                                .font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if isSourceMember(d) { pill("source", .green) }
                    }
                    .padding(.vertical, 3)
                }
            }
        }
    }

    // MARK: Bluetooth

    private var bluetoothSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Label("Bluetooth devices", systemImage: "dot.radiowaves.left.and.right").font(.headline)
                Spacer()
                Button {
                    Task { await refresh() }
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .buttonStyle(.borderless)
            }
            if paired.isEmpty {
                Text(btPowered == false ? "Bluetooth is off" : "No paired devices")
                    .font(.system(size: 12)).foregroundStyle(.secondary)
            } else {
                ForEach(paired) { p in
                    HStack {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(p.name).font(.system(size: 13))
                            Text(p.address).font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                        Spacer()
                        pill(
                            connectedAddrs.contains(p.address) ? "connected" : "disconnected",
                            connectedAddrs.contains(p.address) ? .green : .gray)
                    }
                    .padding(.vertical, 3)
                }
            }
        }
    }

    private func pill(_ text: String, _ color: Color) -> some View {
        Text(text).font(.system(size: 11))
            .padding(.horizontal, 7).padding(.vertical, 1)
            .background(Capsule().fill(color.opacity(0.16)))
            .foregroundStyle(color)
    }

    private func isSourceMember(_ d: USBDeviceInfo) -> Bool {
        guard let s = store.config.source else { return false }
        return s.vendorID == d.vendorID && s.productIDs.contains(d.productID)
    }

    private func refresh() async {
        usb.refreshAttached()
        btPowered = await controller.bt.isPoweredOn()
        let list = await controller.bt.pairedDevices()
        var conn = Set<String>()
        for d in list {
            if await controller.bt.isConnected(d.address) { conn.insert(d.address) }
        }
        paired = list
        connectedAddrs = conn
    }
}
