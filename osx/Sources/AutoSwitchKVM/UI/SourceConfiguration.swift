import SwiftUI

func usbKey(_ vendorID: UInt16, _ productID: UInt16) -> UInt32 { (UInt32(vendorID) << 16) | UInt32(productID) }
func usbKey(_ d: USBDeviceInfo) -> UInt32 { usbKey(d.vendorID, d.productID) }

extension USBSource {
    /// Build a source from selected USB devices, requiring exactly one vendor (the model is
    /// one vendor + product IDs). Returns nil if no devices or multiple vendors are selected.
    static func from(name: String, devices: [USBDeviceInfo]) -> USBSource? {
        let vendors = Set(devices.map(\.vendorID))
        guard vendors.count == 1, let vendor = vendors.first else { return nil }
        let pids = Set(devices.map(\.productID))
        guard !pids.isEmpty else { return nil }
        return USBSource(name: name.isEmpty ? "Source" : name, vendorID: vendor, productIDs: pids)
    }
}

/// Reusable editor: a name field plus a checkbox list of candidate USB devices. Used by both the
/// manual flow and the Learn confirm step. Enforces a single vendor with an inline warning.
struct SourceEditorView: View {
    let candidates: [USBDeviceInfo]
    @Binding var name: String
    @Binding var selectedKeys: Set<UInt32>

    private var selectedDevices: [USBDeviceInfo] { candidates.filter { selectedKeys.contains(usbKey($0)) } }
    private var vendorCount: Int { Set(selectedDevices.map(\.vendorID)).count }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Name").frame(width: 48, alignment: .leading)
                TextField("e.g. Desk KVM", text: $name)
            }

            Text(
                "Select the device(s) that belong to this source. Leave out anything that isn’t part of the switch (e.g. a mouse plugged into it)."
            )
            .font(.caption).foregroundStyle(.secondary)

            if candidates.isEmpty {
                Text("No devices detected.").foregroundStyle(.secondary).padding(.vertical, 8)
            } else {
                List {
                    ForEach(candidates) { dev in
                        let k = usbKey(dev)
                        Toggle(
                            isOn: Binding(
                                get: { selectedKeys.contains(k) },
                                set: { on in if on { selectedKeys.insert(k) } else { selectedKeys.remove(k) } }
                            )
                        ) {
                            VStack(alignment: .leading, spacing: 1) {
                                Text(dev.name.isEmpty ? "Unknown device" : dev.name)
                                Text(String(format: "0x%04X:0x%04X", dev.vendorID, dev.productID))
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }
                }
                .frame(minHeight: 150)
            }

            if vendorCount > 1 {
                Label(
                    "A source must be a single vendor. Please select devices from only one vendor.",
                    systemImage: "exclamationmark.triangle"
                )
                .font(.caption).foregroundStyle(.orange)
            }
        }
    }
}

/// Manual configuration: choose source devices from the currently-attached list.
struct ManualSourceSheet: View {
    let candidates: [USBDeviceInfo]
    let existing: USBSource?
    let onSave: (USBSource) -> Void
    @Environment(\.dismiss) private var dismiss

    @State private var name = ""
    @State private var selectedKeys: Set<UInt32> = []

    private var selectedDevices: [USBDeviceInfo] { candidates.filter { selectedKeys.contains(usbKey($0)) } }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Configure source").font(.headline)
            SourceEditorView(candidates: candidates, name: $name, selectedKeys: $selectedKeys)
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Save") {
                    if let source = USBSource.from(name: name, devices: selectedDevices) {
                        onSave(source); dismiss()
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(USBSource.from(name: name, devices: selectedDevices) == nil)
            }
        }
        .padding()
        .frame(width: 470)
        .onAppear {
            name = existing?.name ?? ""
            if let s = existing {
                selectedKeys = Set(
                    candidates
                        .filter { $0.vendorID == s.vendorID && s.productIDs.contains($0.productID) }
                        .map { usbKey($0) })
            }
        }
    }
}

/// Learn flow: detect source devices by switching the KVM, then confirm/name them.
struct LearnSourceSheet: View {
    let existingName: String
    let onSave: (USBSource) -> Void
    @EnvironmentObject var learner: SourceLearner
    @Environment(\.dismiss) private var dismiss

    private enum Phase { case learning, confirm }
    @State private var phase: Phase = .learning
    @State private var candidates: [USBDeviceInfo] = []
    @State private var name = ""
    @State private var selectedKeys: Set<UInt32> = []

    private var selectedDevices: [USBDeviceInfo] { candidates.filter { selectedKeys.contains(usbKey($0)) } }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            switch phase {
            case .learning: learningView
            case .confirm: confirmView
            }
        }
        .padding()
        .frame(width: 480)
        .onAppear { name = existingName }
    }

    private var learningView: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Learn source").font(.headline)
            Text(
                "Now switch your KVM to another computer and back — or just away. The app notes which USB devices appear or disappear, then lets you confirm them."
            )
            .font(.callout).foregroundStyle(.secondary)
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text("\(learner.changeCount) device change(s) detected")
                    .font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                Spacer()
                Button("Cancel") {
                    learner.cancel(); dismiss()
                }
                Button("Done") {
                    candidates = learner.finish()
                    selectedKeys = Set(candidates.map { usbKey($0) })
                    phase = .confirm
                }
                .keyboardShortcut(.defaultAction)
                .disabled(learner.changeCount == 0)
            }
        }
    }

    private var confirmView: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Confirm source").font(.headline)
            SourceEditorView(candidates: candidates, name: $name, selectedKeys: $selectedKeys)
            HStack {
                Button("Back") {
                    learner.start(); phase = .learning
                }
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Save") {
                    if let source = USBSource.from(name: name, devices: selectedDevices) {
                        onSave(source); dismiss()
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(USBSource.from(name: name, devices: selectedDevices) == nil)
            }
        }
    }
}
