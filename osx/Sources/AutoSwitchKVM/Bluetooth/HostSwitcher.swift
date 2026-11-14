import Foundation

/// Host-initiated channel switching for multi-host devices that support it (SPECIFICATION.md §5a
/// type 5, "Switches channel on request": Logitech HID++ 2.0 feature 0x1814 "Change Host", and ZMK
/// keyboards running the AutoSwitch firmware module; `CompositeHostSwitcher` puts both behind one). An
/// optional service: the engine works without it. Only the host the device is connected to can
/// talk to it, which suits the release side of a handoff. Mirrors Windows' `IHostSwitcher`.
@MainActor
protocol HostSwitcher: AnyObject, Sendable {
    /// The device's channels, or nil when it doesn't support channel switching or isn't reachable
    /// right now (not connected to this Mac).
    func channels(_ address: String) async throws -> HostChannels?

    /// Send the device to `channel` (0-based). It leaves this Mac right after.
    func switchTo(_ address: String, channel: Int) async throws

    /// Mark the device's current channel as "AutoSwitch runs here", so the other computer's
    /// AutoSwitch can find it when it releases the device. Returns the channel it marked
    /// (0-based), or nil when it was already marked or the device doesn't support channels.
    @discardableResult
    func markCurrentChannel(_ address: String) async throws -> Int?

    /// The name the device stores for each channel (Logitech HID++ 0x1815 "Hosts Info"): what
    /// each channel's computer called itself when it paired. nil when the device doesn't keep
    /// names or isn't reachable; a nil entry for a channel without one. For Settings only,
    /// never on the release path.
    func channelNames(_ address: String) async throws -> [String?]?
}

extension HostSwitcher {
    func channelNames(_ address: String) async throws -> [String?]? { nil }
}

/// A device's channels: how many, which one this Mac is on (0-based), and one mark byte per
/// channel (HID++ "cookies", ZMK module marks: stored on the device, cleared by the firmware when a
/// channel is paired with a new host).
struct HostChannels: Equatable, Sendable {
    /// The byte AutoSwitch writes on its own channel (the same on both platforms).
    static let autoSwitchMark: UInt8 = 0xA5

    let count: Int
    let current: Int
    let marks: [UInt8]
    /// Per channel: is it paired with a host? nil when the device doesn't say. HID++ devices with
    /// Hosts Info (0x1815) and the ZMK module report it, and an unpaired channel is never a target:
    /// the device would just advertise there for a new pairing.
    var paired: [Bool]? = nil
    /// What the device calls its channels in Settings and the log: "Channel" (HID++), or
    /// "Profile" (the ZMK module's protocol v1, whose channels are Bluetooth profiles).
    var noun = "Channel"

    /// Whether channel `n` is known to be unpaired.
    func isUnpaired(_ n: Int) -> Bool {
        paired.map { n < $0.count && !$0[n] } ?? false
    }

    /// The single other paired channel, when the device reports pairing (ZMK) and exactly one
    /// channel besides this Mac's is paired; else nil.
    var onlyOtherPairedChannel: Int? {
        guard let paired else { return nil }
        let others = (0..<min(count, paired.count)).filter { $0 != current && paired[$0] }
        return others.count == 1 ? others[0] : nil
    }

    /// Where "automatic" sends the device: the single other channel marked by AutoSwitch; with no
    /// mark at all, the only other paired channel (a ZMK keyboard paired with just these two
    /// computers works before the other computer's AutoSwitch has marked its profile). Same rule
    /// as Windows' `HostChannels.AutomaticTarget`.
    var automaticTarget: Int? {
        if let marked = markedOtherChannel { return marked }
        let anyOtherMarked = (0..<min(count, marks.count)).contains {
            $0 != current && marks[$0] == Self.autoSwitchMark
        }
        return anyOtherMarked ? nil : onlyOtherPairedChannel
    }

    /// The single other channel marked by AutoSwitch, or nil if none (or several: ambiguous).
    var markedOtherChannel: Int? {
        let marked = (0..<min(count, marks.count)).filter { $0 != current && marks[$0] == Self.autoSwitchMark }
        return marked.count == 1 ? marked[0] : nil
    }

    /// "Channel 2 · Engin's PC" (0-based `channel`), or "Channel 2" without a stored name.
    /// `noun` is "Profile" for a ZMK keyboard.
    static func label(_ channel: Int, names: [String?]?, noun: String = "Channel") -> String {
        let name = names.flatMap { channel < $0.count ? $0[channel] : nil }
        return name.map { "\(noun) \(channel + 1) · \($0)" } ?? "\(noun) \(channel + 1)"
    }

    /// This host's channel from the names the device stores (HID++ Hosts Info): the one channel
    /// carrying `myName`; else, when pairing is known, the only paired channel without a name (the
    /// other computers' AutoSwitch named theirs). nil when that doesn't single one out.
    static func ownChannel(names: [String?]?, paired: [Bool]?, myName: String) -> Int? {
        guard let names else { return nil }
        let mine = names.indices.filter { names[$0].map { nameMatches($0, myName) } ?? false }
        if mine.count == 1 { return mine[0] }
        guard mine.isEmpty, let paired else { return nil }
        let unnamed = paired.indices.filter { paired[$0] && ($0 >= names.count || names[$0] == nil) }
        return unnamed.count == 1 ? unnamed[0] : nil
    }

    /// A stored name is this computer's: equal, or a cut-down copy (devices cap the length).
    static func nameMatches(_ stored: String, _ myName: String) -> Bool {
        stored == myName || (stored.count >= 8 && myName.hasPrefix(stored))
    }

    /// A channel name from Hosts Info name pieces (UTF-8, `length` bytes in all): trailing NULs
    /// and spaces dropped, nil when empty.
    static func decodeName(_ bytes: [UInt8], length: Int) -> String? {
        let name = String(decoding: bytes.prefix(length), as: UTF8.self)
            .trimmingCharacters(in: CharacterSet(charactersIn: "\0").union(.whitespacesAndNewlines))
        return name.isEmpty ? nil : name
    }
}

/// Several channel-switching backends behind one `HostSwitcher` (Logitech HID++, the AutoSwitch
/// ZMK module). The first backend that answers for a device owns it from then on (a device's
/// protocol never changes). Mirrors Windows' `CompositeHostSwitcher`.
@MainActor
final class CompositeHostSwitcher: HostSwitcher {
    private let backends: [HostSwitcher]
    private var owner: [String: HostSwitcher] = [:]

    init(_ backends: [HostSwitcher]) { self.backends = backends }

    private static func key(_ address: String) -> String {
        address.replacingOccurrences(of: ":", with: "").replacingOccurrences(of: "-", with: "").uppercased()
    }

    func channels(_ address: String) async throws -> HostChannels? {
        if let owner = owner[Self.key(address)] { return try await owner.channels(address) }
        for backend in backends {
            guard let channels = try await backend.channels(address) else { continue }
            owner[Self.key(address)] = backend
            return channels
        }
        return nil
    }

    func switchTo(_ address: String, channel: Int) async throws {
        guard let owner = try await ownerOf(address) else {
            throw BTError.classified(
                .deviceNotFound,
                "the device doesn't answer any channel-switching protocol (not connected to this Mac?)")
        }
        try await owner.switchTo(address, channel: channel)
    }

    @discardableResult
    func markCurrentChannel(_ address: String) async throws -> Int? {
        try await ownerOf(address)?.markCurrentChannel(address)
    }

    func channelNames(_ address: String) async throws -> [String?]? {
        try await ownerOf(address)?.channelNames(address)
    }

    private func ownerOf(_ address: String) async throws -> HostSwitcher? {
        if let owner = owner[Self.key(address)] { return owner }
        _ = try await channels(address)
        return owner[Self.key(address)]
    }
}
