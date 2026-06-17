import Foundation

/// The "switcher source": a single USB vendor plus the set of product IDs that appear when this
/// Mac is selected on the KVM (a hub often exposes several product IDs on one vendor). Named by
/// the user. The Mac is considered selected when ANY of these product IDs is attached.
struct USBSource: Codable, Hashable, Identifiable {
    var name: String
    var vendorID: UInt16
    var productIDs: Set<UInt16>

    var id: String { "\(vendorID)-\(productIDs.sorted().map(String.init).joined(separator: ","))" }

    /// "0x05E3 : 0x0610, 0x0626"
    var displayVidPid: String {
        let pids = productIDs.sorted().map { String(format: "0x%04X", $0) }.joined(separator: ", ")
        return String(format: "0x%04X : %@", vendorID, pids)
    }
}

/// A Bluetooth device managed by the app.
struct BTDevice: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var name: String
    /// BT MAC address. IOBluetooth wants "-" separators (e.g. "3c-50-02-bf-22-45").
    var address: String
    /// Whether this device participates in automatic handoff.
    var enabled: Bool = true
    /// If true: pair before connecting, unpair after disconnecting.
    /// Needed for devices like the Magic Trackpad that won't reconnect across hosts otherwise.
    var managePairing: Bool = false
    /// Delay (ms) before connecting this device, to stagger a handoff. Connect order is the
    /// position in the profile's device list.
    var connectDelayMs: Int = 0

    /// Normalize to the dash-separated lowercase form IOBluetooth prefers.
    var normalizedAddress: String {
        address.replacingOccurrences(of: ":", with: "-").lowercased()
    }
}

extension BTDevice {
    enum CodingKeys: String, CodingKey {
        case id, name, address, enabled, managePairing, connectDelayMs
    }

    /// Backward-compatible decoding: missing keys fall back to defaults (Swift's synthesized
    /// `Decodable` would otherwise throw), so configs saved before a field was added still load.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? ""
        address = try c.decodeIfPresent(String.self, forKey: .address) ?? ""
        enabled = try c.decodeIfPresent(Bool.self, forKey: .enabled) ?? true
        managePairing = try c.decodeIfPresent(Bool.self, forKey: .managePairing) ?? false
        connectDelayMs = try c.decodeIfPresent(Int.self, forKey: .connectDelayMs) ?? 0
    }
}

/// A global keyboard shortcut: a virtual key code + Carbon modifier mask, plus a display string
/// (e.g. "⌃⌥⌘P") captured when recorded. Conversion from `NSEvent` and the defaults live in
/// `HotKeyManager.swift` (which has Carbon/AppKit).
struct KeyShortcut: Codable, Hashable {
    var keyCode: UInt32
    var carbonModifiers: UInt32
    var display: String
}

/// A named configuration: a source plus its managed devices. Switchable from the menu / Settings.
/// Global options (timing, notifications, pause, dock) live on `AppConfig`, not here.
struct Profile: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var name: String
    var source: USBSource?
    var devices: [BTDevice] = []
}

/// Whole-app configuration, persisted as JSON. Holds the list of profiles + which is active,
/// plus app-wide options. `source` / `devices` are computed accessors onto the active profile so
/// existing call sites stay simple.
struct AppConfig: Codable {
    var profiles: [Profile]
    var activeProfileID: UUID

    /// Debounce before reacting to the source *disappearing* (avoids disconnecting on brief USB
    /// blips during a switch).
    var debounceMs: Int
    /// Debounce before reacting to the source *appearing* — typically shorter so connect is snappy.
    var arrivalDebounceMs: Int
    var connectRetryMax: Int
    /// Base retry interval (seconds). Backoff between attempts is `connectRetrySecs * 2^(attempt-1)`.
    var connectRetrySecs: Int
    var btCallTimeoutSecs: Int
    var showNotifications: Bool
    /// Notify when a managed device drops while the source is still present (e.g. another host
    /// claimed it). Off by default.
    var notifyUnexpectedDisconnect: Bool
    var launchAtLogin: Bool
    /// When true, automatic handoff is suspended (manual actions still work).
    var paused: Bool
    var dockAutoHide: Bool
    /// System-wide keyboard shortcuts. Off by default; each combo is user-assignable.
    var globalHotkeysEnabled: Bool
    var hotkeyPause: KeyShortcut?
    var hotkeyConnectAll: KeyShortcut?
    var hotkeyDisconnectAll: KeyShortcut?

    init(
        profiles: [Profile] = [Profile(name: "Default")],
        activeProfileID: UUID? = nil,
        debounceMs: Int = 1200,
        arrivalDebounceMs: Int = 400,
        connectRetryMax: Int = 6,
        connectRetrySecs: Int = 2,
        btCallTimeoutSecs: Int = 15,
        showNotifications: Bool = false,
        notifyUnexpectedDisconnect: Bool = false,
        launchAtLogin: Bool = false,
        paused: Bool = false,
        dockAutoHide: Bool = false,
        globalHotkeysEnabled: Bool = false,
        hotkeyPause: KeyShortcut? = .defaultPause,
        hotkeyConnectAll: KeyShortcut? = .defaultConnectAll,
        hotkeyDisconnectAll: KeyShortcut? = .defaultDisconnectAll
    ) {
        let list = profiles.isEmpty ? [Profile(name: "Default")] : profiles
        self.profiles = list
        self.activeProfileID =
            (activeProfileID.flatMap { id in list.contains { $0.id == id } ? id : nil }) ?? list[0].id
        self.debounceMs = debounceMs
        self.arrivalDebounceMs = arrivalDebounceMs
        self.connectRetryMax = connectRetryMax
        self.connectRetrySecs = connectRetrySecs
        self.btCallTimeoutSecs = btCallTimeoutSecs
        self.showNotifications = showNotifications
        self.notifyUnexpectedDisconnect = notifyUnexpectedDisconnect
        self.launchAtLogin = launchAtLogin
        self.paused = paused
        self.dockAutoHide = dockAutoHide
        self.globalHotkeysEnabled = globalHotkeysEnabled
        self.hotkeyPause = hotkeyPause
        self.hotkeyConnectAll = hotkeyConnectAll
        self.hotkeyDisconnectAll = hotkeyDisconnectAll
    }

    static let `default` = AppConfig()

    // MARK: Active-profile accessors (not stored; keep call sites simple)

    var activeProfile: Profile? { profiles.first { $0.id == activeProfileID } }

    var source: USBSource? {
        get { activeProfile?.source }
        set { mutateActive { $0.source = newValue } }
    }
    var devices: [BTDevice] {
        get { activeProfile?.devices ?? [] }
        set { mutateActive { $0.devices = newValue } }
    }
    var activeProfileName: String {
        get { activeProfile?.name ?? "" }
        set { mutateActive { $0.name = newValue } }
    }

    private mutating func mutateActive(_ change: (inout Profile) -> Void) {
        guard let idx = profiles.firstIndex(where: { $0.id == activeProfileID }) else { return }
        change(&profiles[idx])
    }

    // MARK: Codable — custom, to migrate legacy single-source configs into a "Default" profile.

    enum CodingKeys: String, CodingKey {
        case profiles, activeProfileID
        case debounceMs, arrivalDebounceMs, connectRetryMax, connectRetrySecs, btCallTimeoutSecs
        case showNotifications, notifyUnexpectedDisconnect, launchAtLogin, paused, dockAutoHide
        case globalHotkeysEnabled, hotkeyPause, hotkeyConnectAll, hotkeyDisconnectAll
        case source, devices  // legacy top-level keys (pre-profiles)
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        debounceMs = try c.decodeIfPresent(Int.self, forKey: .debounceMs) ?? 1200
        arrivalDebounceMs = try c.decodeIfPresent(Int.self, forKey: .arrivalDebounceMs) ?? 400
        connectRetryMax = try c.decodeIfPresent(Int.self, forKey: .connectRetryMax) ?? 6
        connectRetrySecs = try c.decodeIfPresent(Int.self, forKey: .connectRetrySecs) ?? 2
        btCallTimeoutSecs = try c.decodeIfPresent(Int.self, forKey: .btCallTimeoutSecs) ?? 15
        showNotifications = try c.decodeIfPresent(Bool.self, forKey: .showNotifications) ?? false
        notifyUnexpectedDisconnect = try c.decodeIfPresent(Bool.self, forKey: .notifyUnexpectedDisconnect) ?? false
        launchAtLogin = try c.decodeIfPresent(Bool.self, forKey: .launchAtLogin) ?? false
        paused = try c.decodeIfPresent(Bool.self, forKey: .paused) ?? false
        dockAutoHide = try c.decodeIfPresent(Bool.self, forKey: .dockAutoHide) ?? false
        globalHotkeysEnabled = try c.decodeIfPresent(Bool.self, forKey: .globalHotkeysEnabled) ?? false
        // Absent key (old config) → the default combo; present-but-null → intentionally cleared.
        hotkeyPause =
            c.contains(.hotkeyPause)
            ? try c.decodeIfPresent(KeyShortcut.self, forKey: .hotkeyPause) : .defaultPause
        hotkeyConnectAll =
            c.contains(.hotkeyConnectAll)
            ? try c.decodeIfPresent(KeyShortcut.self, forKey: .hotkeyConnectAll) : .defaultConnectAll
        hotkeyDisconnectAll =
            c.contains(.hotkeyDisconnectAll)
            ? try c.decodeIfPresent(KeyShortcut.self, forKey: .hotkeyDisconnectAll) : .defaultDisconnectAll

        var list = try c.decodeIfPresent([Profile].self, forKey: .profiles) ?? []
        if list.isEmpty {
            let legacySource = try c.decodeIfPresent(USBSource.self, forKey: .source)
            let legacyDevices = try c.decodeIfPresent([BTDevice].self, forKey: .devices) ?? []
            list = [Profile(name: "Default", source: legacySource, devices: legacyDevices)]
        }
        profiles = list
        let saved = try c.decodeIfPresent(UUID.self, forKey: .activeProfileID)
        activeProfileID = (saved.flatMap { id in list.contains { $0.id == id } ? id : nil }) ?? list[0].id
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(profiles, forKey: .profiles)
        try c.encode(activeProfileID, forKey: .activeProfileID)
        try c.encode(debounceMs, forKey: .debounceMs)
        try c.encode(arrivalDebounceMs, forKey: .arrivalDebounceMs)
        try c.encode(connectRetryMax, forKey: .connectRetryMax)
        try c.encode(connectRetrySecs, forKey: .connectRetrySecs)
        try c.encode(btCallTimeoutSecs, forKey: .btCallTimeoutSecs)
        try c.encode(showNotifications, forKey: .showNotifications)
        try c.encode(notifyUnexpectedDisconnect, forKey: .notifyUnexpectedDisconnect)
        try c.encode(launchAtLogin, forKey: .launchAtLogin)
        try c.encode(paused, forKey: .paused)
        try c.encode(dockAutoHide, forKey: .dockAutoHide)
        try c.encode(globalHotkeysEnabled, forKey: .globalHotkeysEnabled)
        try c.encode(hotkeyPause, forKey: .hotkeyPause)
        try c.encode(hotkeyConnectAll, forKey: .hotkeyConnectAll)
        try c.encode(hotkeyDisconnectAll, forKey: .hotkeyDisconnectAll)
    }
}
