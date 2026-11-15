import AppKit
import Combine
import SwiftUI

/// The switch status panel (osx/docs/superpowers/specs/2026-11-16-switch-status-panel-design.md):
/// a small floating panel at the top-right, under the menu bar, that shows each device switching
/// and hides by itself (`SwitchStatusModel` decides what and when). It never becomes key or main,
/// so it never takes focus from what the user is typing in. Mirrors Windows' `StatusPopup`.
@MainActor
final class SwitchStatusPanel {
    static let width: CGFloat = 320
    private static let inset: CGFloat = 8

    private let model: SwitchStatusModel
    private let openSettings: () -> Void
    private var panel: NonActivatingPanel?
    private var host: NSHostingView<SwitchStatusView>?
    private var timer: Timer?
    private var presence = PanelPresence()
    private var cancellables = Set<AnyCancellable>()

    init(model: SwitchStatusModel, openSettings: @escaping () -> Void) {
        self.model = model
        self.openSettings = openSettings
    }

    func start() {
        let host = NSHostingView(
            rootView: SwitchStatusView(model: model) { [weak self] in
                self?.openSettings()
                self?.model.dismiss()
            })
        let panel = NonActivatingPanel(
            contentRect: NSRect(x: 0, y: 0, width: Self.width, height: 60),
            styleMask: [.nonactivatingPanel, .borderless], backing: .buffered, defer: true)
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.hidesOnDeactivate = false
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        self.panel = panel
        self.host = host

        model.$rows.receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.place() }
            .store(in: &cancellables)
        // Ticks also run while a menu is tracking (common modes).
        let timer = Timer(timeInterval: 0.25, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    /// Hover, from the pointer position: tracking areas don't fire in an inactive app's panel.
    private var hovering: Bool {
        guard let panel, presence.shown else { return false }
        return NSMouseInRect(NSEvent.mouseLocation, panel.frame, false)
    }

    /// The menu bar panel shows the same rows; don't cover it.
    private var menuOpen: Bool {
        NSApp.windows.contains { $0.isVisible && String(describing: type(of: $0)).contains("MenuBarExtra") }
    }

    private func tick() {
        model.tick(now: Date(), hovering: hovering)
        guard let panel else { return }
        switch presence.step(wanted: model.visible && !model.rows.isEmpty && !menuOpen) {
        case .show:
            place()
            if !panel.isVisible { panel.alphaValue = 0 }
            panel.orderFrontRegardless()  // never makeKey: no focus taken
            NSAnimationContext.runAnimationGroup {
                $0.duration = 0.15; panel.animator().alphaValue = 1
            }
        case .hide:
            NSAnimationContext.runAnimationGroup {
                $0.duration = 0.15; panel.animator().alphaValue = 0
            } completionHandler: {
                MainActor.assumeIsolated { if self.presence.orderOutAfterFade { panel.orderOut(nil) } }
            }
        case .none:
            break
        }
    }

    /// Top-right of the screen with the menu bar, sized to its content.
    private func place() {
        guard let panel, let host, let screen = NSScreen.screens.first else { return }
        host.layoutSubtreeIfNeeded()
        let height = max(40, host.fittingSize.height)
        let frame = screen.visibleFrame
        panel.setFrame(
            NSRect(
                x: frame.maxX - Self.width - Self.inset, y: frame.maxY - height - Self.inset, width: Self.width,
                height: height), display: true)
    }
}

/// Whether the panel should be shown, from the panel's own state rather than `isVisible` (which
/// stays true while a fade-out runs: rows arriving then left it ordered in at alpha 0).
struct PanelPresence {
    enum Action { case show, hide, none }
    private(set) var shown = false

    mutating func step(wanted: Bool) -> Action {
        guard wanted != shown else { return .none }
        shown = wanted
        return wanted ? .show : .hide
    }

    /// When a fade-out ends: order the panel out only if it wasn't shown again meanwhile.
    var orderOutAfterFade: Bool { !shown }
}

/// Never key, never main: showing it doesn't move focus.
final class NonActivatingPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

struct SwitchStatusView: View {
    @ObservedObject var model: SwitchStatusModel
    let onClick: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            if !model.header.isEmpty {
                Text(model.header).font(.system(size: 12, weight: .semibold)).foregroundStyle(.secondary)
            }
            ForEach(model.rows) { row in
                HStack(spacing: 10) {
                    Image(systemName: row.symbol)
                        .font(.system(size: 14)).foregroundStyle(Color.accentColor).frame(width: 20)
                    Text(row.name).font(.system(size: 13, weight: .medium)).lineLimit(1)
                    Spacer(minLength: 8)
                    toneMark(row.tone)
                    Text(row.text)
                        .font(.system(size: 12))
                        .foregroundStyle(row.tone == .problem ? Color.orange : Color.secondary)
                        .lineLimit(2).multilineTextAlignment(.trailing)
                }
            }
        }
        .padding(14)
        .frame(width: SwitchStatusPanel.width, alignment: .leading)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.quaternary, lineWidth: 0.5))
        .contentShape(Rectangle())
        .onTapGesture(perform: onClick)
    }

    @ViewBuilder private func toneMark(_ tone: SwitchStatusRow.Tone) -> some View {
        switch tone {
        case .busy: ProgressView().controlSize(.small).scaleEffect(0.7).frame(width: 14, height: 14)
        case .ok: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
        case .calm: Image(systemName: "moon.zzz").foregroundStyle(.secondary)
        case .problem: Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
        }
    }
}
