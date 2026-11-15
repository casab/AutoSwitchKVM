import Foundation

/// One device's line in the switch status panel.
struct SwitchStatusRow: Identifiable, Equatable {
    enum Tone { case busy, ok, calm, problem }
    let id: UUID
    var name: String
    var symbol: String
    var text: String
    var tone: Tone
}

/// The switch status panel's logic (osx/docs/superpowers/specs/2026-11-16-switch-status-panel-design.md;
/// mirrors Windows' `StatusPopup`): which devices get a row, what each row says, and when the panel
/// hides. `SwitchStatusPanel` shows it; this type has no UI so it can be tested.
@MainActor
final class SwitchStatusModel: ObservableObject {
    @Published private(set) var rows: [SwitchStatusRow] = []
    @Published private(set) var header = ""
    @Published private(set) var visible = false
    /// Show devices switching (the `showNotifications` setting, "Switch status").
    var showSwitches = false
    /// Show unexpected drops (the `notifyUnexpectedDisconnect` setting), even when switches aren't.
    var showDrops = false

    /// How long the panel stays after the last change once nothing is connecting.
    static let holdSeconds: TimeInterval = 3.5
    static let holdWithProblemSeconds: TimeInterval = 8

    private enum Kind { case idle, connecting, connected, disconnected, bluetoothOff, error }

    private var lastKinds: [UUID: Kind] = [:]
    private var lastSelected: Bool?
    private var lastChange = Date.distantPast
    /// Rows are added only after a KVM move seen since the engine's first evaluation: launch, wake
    /// and profile switches connect everything without the KVM having moved.
    private var inBurst = false
    /// Drop rows keep their text while the device stays disconnected (the engine reports the drop
    /// right after setting it Disconnected).
    private var dropped = Set<UUID>()
    /// Devices that ended up not connected (or with a problem) while the KVM is here: reported
    /// once, then left out (their retries, later switches) until they connect again (Windows'
    /// `StatusChangeFilter`, 2026-11-16: a switched-off speaker kept popping the panel).
    private var quiet = Set<UUID>()

    /// Feed the engine's current state. Until the engine has evaluated presence (`evaluated`), and
    /// at its first evaluation, this only records it.
    func update(
        devices: [BTDevice], statuses: [UUID: DeviceStatus], selected: Bool, evaluated: Bool, asleep: Set<UUID>,
        sentAway: Set<UUID>, now: Date
    ) {
        let ids = Set(devices.map(\.id))
        rows.removeAll { !ids.contains($0.id) }
        lastKinds = lastKinds.filter { ids.contains($0.key) }

        guard evaluated, let previous = lastSelected else {
            lastSelected = evaluated ? selected : nil
            for d in devices { if let s = statuses[d.id] { lastKinds[d.id] = Self.kind(s) } }
            return
        }
        if previous != selected {  // the KVM moved: a new burst
            lastSelected = selected
            header = selected ? "KVM → this Mac" : "KVM → the other computer"
            rows = []
            dropped = []
            inBurst = true
        }

        var changed = false
        for d in devices {
            guard let status = statuses[d.id] else { continue }
            let new = Self.kind(status)
            let old = lastKinds[d.id]
            lastKinds[d.id] = new
            if new == .connected { quiet.remove(d.id) }
            let line = Self.line(
                d, status, selected: selected, asleep: asleep.contains(d.id), sent: sentAway.contains(d.id))
            if dropped.contains(d.id) {
                if new == .disconnected { continue }
                dropped.remove(d.id)
            }
            if let i = rows.firstIndex(where: { $0.id == d.id }) {
                if rows[i] != line {  // live update of a row already shown
                    rows[i] = line
                    changed = true
                }
                continue
            }
            guard inBurst, showSwitches, old != new, new != .idle, !quiet.contains(d.id) else { continue }
            // A device first seen at rest (e.g. just added) didn't switch.
            guard !(old == nil && (new == .connected || new == .disconnected)) else { continue }
            rows.append(line)
            changed = true
        }
        // Report a device that ended up not connected while here once (after its row was added or
        // updated above), then keep it quiet.
        for d in devices {
            guard let status = statuses[d.id], rows.contains(where: { $0.id == d.id }) else { continue }
            let k = Self.kind(status)
            if k == .error || k == .bluetoothOff || (selected && k == .disconnected) { quiet.insert(d.id) }
        }
        if changed {
            lastChange = now
            if showSwitches { visible = true }
        }
    }

    /// A device dropped while the KVM is here (shown when `showDrops`).
    func noteDrop(_ device: BTDevice, now: Date) {
        guard showDrops else { return }
        let line = SwitchStatusRow(
            id: device.id, name: device.name, symbol: DeviceRowCard.symbol(for: device.name),
            text: "Dropped unexpectedly", tone: .problem)
        if let i = rows.firstIndex(where: { $0.id == device.id }) { rows[i] = line } else { rows.append(line) }
        dropped.insert(device.id)
        lastChange = now
        visible = true
    }

    /// Hide once things have settled: nothing connecting, the hold time passed, not hovered.
    func tick(now: Date, hovering: Bool) {
        guard visible, !hovering, !rows.contains(where: { $0.tone == .busy }) else { return }
        let hold = rows.contains { $0.tone == .problem } ? Self.holdWithProblemSeconds : Self.holdSeconds
        if now.timeIntervalSince(lastChange) >= hold { dismiss() }
    }

    func dismiss() {
        visible = false
        rows = []
        dropped = []
    }

    private static func kind(_ s: DeviceStatus) -> Kind {
        switch s {
        case .idle: return .idle
        case .connecting: return .connecting
        case .connected: return .connected
        case .disconnected: return .disconnected
        case .bluetoothOff: return .bluetoothOff
        case .error: return .error
        }
    }

    private static func line(
        _ d: BTDevice, _ s: DeviceStatus, selected: Bool, asleep: Bool, sent: Bool
    ) -> SwitchStatusRow {
        let (text, tone): (String, SwitchStatusRow.Tone) =
            switch s {
            case .idle: ("Idle", .ok)
            case .connecting: ("Connecting…", .busy)
            case .connected: ("Connected", .ok)
            case .disconnected:
                if asleep {
                    ("Asleep: wake it to connect", .calm)
                } else if selected {
                    // KVM here: a channel switcher that didn't come over (the engine's arrival wait
                    // ended), or a device that was let go.
                    d.managePairing ? ("Disconnected", .ok) : ("Didn't come over: press its channel key", .calm)
                } else if d.managePairing {
                    ("Released", .ok)
                } else {
                    sent ? ("Sent to the other computer", .ok) : ("Disconnected", .ok)
                }
            case .bluetoothOff: ("Bluetooth is off", .problem)
            case .error(let m): (m, .problem)
            }
        return SwitchStatusRow(
            id: d.id, name: d.name, symbol: DeviceRowCard.symbol(for: d.name), text: text, tone: tone)
    }
}
