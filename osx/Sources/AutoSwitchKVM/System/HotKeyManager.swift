import AppKit
import Carbon.HIToolbox
import Foundation

extension KeyShortcut {
    /// Default combos (⌃⌥⌘ P/C/D), used until the user assigns their own.
    static let defaultPause = KeyShortcut(
        keyCode: UInt32(kVK_ANSI_P),
        carbonModifiers: UInt32(controlKey | optionKey | cmdKey),
        display: "⌃⌥⌘P")
    static let defaultConnectAll = KeyShortcut(
        keyCode: UInt32(kVK_ANSI_C),
        carbonModifiers: UInt32(controlKey | optionKey | cmdKey),
        display: "⌃⌥⌘C")
    static let defaultDisconnectAll = KeyShortcut(
        keyCode: UInt32(kVK_ANSI_D),
        carbonModifiers: UInt32(controlKey | optionKey | cmdKey),
        display: "⌃⌥⌘D")

    /// Build from a recorded key-down event. Requires at least one of ⌃⌥⌘⇧ (a bare key makes a poor
    /// global hotkey); returns nil otherwise.
    init?(event: NSEvent) {
        let flags = event.modifierFlags.intersection([.command, .option, .control, .shift])
        var carbon = 0
        if flags.contains(.command) { carbon |= cmdKey }
        if flags.contains(.option) { carbon |= optionKey }
        if flags.contains(.control) { carbon |= controlKey }
        if flags.contains(.shift) { carbon |= shiftKey }
        guard carbon != 0 else { return nil }

        var d = ""
        if flags.contains(.control) { d += "⌃" }
        if flags.contains(.option) { d += "⌥" }
        if flags.contains(.shift) { d += "⇧" }
        if flags.contains(.command) { d += "⌘" }
        d += (event.charactersIgnoringModifiers ?? "").uppercased()

        self.keyCode = UInt32(event.keyCode)
        self.carbonModifiers = UInt32(carbon)
        self.display = d
    }
}

/// Registers user-assigned system-wide hotkeys via Carbon `RegisterEventHotKey` (no Accessibility
/// permission needed). `apply(...)` is idempotent — call it whenever the enabled flag or any
/// shortcut changes.
@MainActor
final class HotKeyManager {
    enum Action: UInt32 { case togglePause = 1, connectAll = 2, disconnectAll = 3 }

    var onAction: ((Action) -> Void)?

    private var hotKeyRefs: [EventHotKeyRef?] = []
    private var handlerRef: EventHandlerRef?
    private let signature: OSType = 0x41534B56  // 'ASKV'

    func apply(enabled: Bool, pause: KeyShortcut?, connectAll: KeyShortcut?, disconnectAll: KeyShortcut?) {
        unregister()
        guard enabled else { return }
        installHandler()
        register(pause, action: .togglePause)
        register(connectAll, action: .connectAll)
        register(disconnectAll, action: .disconnectAll)
    }

    private func installHandler() {
        guard handlerRef == nil else { return }
        var eventType = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard),
            eventKind: UInt32(kEventHotKeyPressed))
        let selfPtr = Unmanaged.passUnretained(self).toOpaque()
        InstallEventHandler(
            GetApplicationEventTarget(),
            { _, event, userData in
                guard let event, let userData else { return noErr }
                var hkID = EventHotKeyID()
                GetEventParameter(
                    event, EventParamName(kEventParamDirectObject),
                    EventParamType(typeEventHotKeyID), nil,
                    MemoryLayout<EventHotKeyID>.size, nil, &hkID)
                let manager = Unmanaged<HotKeyManager>.fromOpaque(userData).takeUnretainedValue()
                MainActor.assumeIsolated { manager.handle(id: hkID.id) }  // dispatched on main
                return noErr
            }, 1, &eventType, selfPtr, &handlerRef)
    }

    private func register(_ shortcut: KeyShortcut?, action: Action) {
        guard let shortcut else { return }
        var ref: EventHotKeyRef?
        let hkID = EventHotKeyID(signature: signature, id: action.rawValue)
        RegisterEventHotKey(
            shortcut.keyCode, shortcut.carbonModifiers, hkID,
            GetApplicationEventTarget(), 0, &ref)
        hotKeyRefs.append(ref)
    }

    private func unregister() {
        for ref in hotKeyRefs { if let ref { UnregisterEventHotKey(ref) } }
        hotKeyRefs.removeAll()
        if let handlerRef { RemoveEventHandler(handlerRef); self.handlerRef = nil }
    }

    private func handle(id: UInt32) {
        if let action = Action(rawValue: id) { onAction?(action) }
    }
}
