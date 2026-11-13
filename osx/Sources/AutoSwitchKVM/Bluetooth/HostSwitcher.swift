import Foundation

/// Host-initiated channel switching for multi-host devices that support it (SPECIFICATION.md §5a
/// type 5, "Switches channel on request": Logitech HID++ 2.0 feature 0x1814 "Change Host"). An
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
}

/// A device's channels: how many, which one this Mac is on (0-based), and one mark byte per
/// channel (HID++ "cookies": stored on the device, cleared by the firmware when a channel is
/// paired with a new host).
struct HostChannels: Equatable, Sendable {
    /// The byte AutoSwitch writes on its own channel (the same on both platforms).
    static let autoSwitchMark: UInt8 = 0xA5

    let count: Int
    let current: Int
    let marks: [UInt8]

    /// The single other channel marked by AutoSwitch, or nil if none (or several: ambiguous).
    var markedOtherChannel: Int? {
        let marked = (0..<min(count, marks.count)).filter { $0 != current && marks[$0] == Self.autoSwitchMark }
        return marked.count == 1 ? marked[0] : nil
    }
}
