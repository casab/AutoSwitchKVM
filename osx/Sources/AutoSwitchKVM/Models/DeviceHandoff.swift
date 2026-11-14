import Foundation

/// How a Bluetooth device handles more than one computer (SPECIFICATION.md §5a). Only some types
/// can be switched by a host. Mirrors Windows' `AutoSwitchKVM.Core/DeviceHandoff.cs`.
enum HandoffType: Equatable, Sendable {
    /// Not enough information: ask the user to test it.
    case unknown
    /// 1. One pairing; once the current host removes it, the next host can pair (Magic Trackpad).
    case pairingHandoff
    /// 2. A pairing per host slot; a key on the device chooses the host (ZMK, Logitech Easy-Switch).
    case devicePicksHost
    /// 3. Remembers one Bluetooth host; changing hosts needs pairing mode (game controllers).
    case repairByHand
    /// 4. Remembers several hosts and joins whichever one connects (many headphones).
    case hostConnects
    /// 5. A pairing per host slot, and the connected host can tell it to switch slot (Logitech
    ///    Easy-Switch with HID++ 0x1814 "Change Host"). Detected live, not by the catalog.
    case channelSwitch
}

enum HandoffConfidence: Equatable, Sendable { case unsure, likely, known }

/// What kind of device it is, from the class of device / minor type (Classic) or appearance (LE).
enum DeviceKind: Equatable, Sendable { case unknown, input, gameController, audio }

/// What a host can learn about a device without talking to it. Vendor/product come from the
/// Device ID record (Classic) or the Device Information service (LE); nil when unknown.
struct DeviceIdentity: Equatable, Sendable {
    var name: String
    var isLowEnergy: Bool = false
    var vendorID: UInt16?
    var productID: UInt16?
    var kind: DeviceKind = .unknown
}

struct HandoffAssessment: Equatable, Sendable {
    var type: HandoffType
    var confidence: HandoffConfidence
    var reason: String

    /// The Manage pairing default when the device is added (spec §5a): on for type 1, and for an
    /// unknown Classic device (the Magic Trackpad case); off for everything else.
    func defaultManagePairing(isLowEnergy: Bool) -> Bool {
        type == .pairingHandoff || (type == .unknown && !isLowEnergy)
    }

    /// The device answered the HID++ Change Host query: type 5, known.
    static let channelSwitch = HandoffAssessment(
        type: .channelSwitch, confidence: .known,
        reason: "Logitech Easy-Switch device that answers the Change Host command.")

    /// The device answered a channel query: type 5, known. A device that reports pairing per
    /// channel is a ZMK keyboard running the AutoSwitch firmware module.
    static func channelSwitch(_ channels: HostChannels) -> HandoffAssessment {
        guard channels.paired != nil else { return .channelSwitch }
        return HandoffAssessment(
            type: .channelSwitch, confidence: .known,
            reason: "ZMK keyboard with the AutoSwitch firmware module: \(channels.count) Bluetooth profiles.")
    }
}

/// Best-effort classification (spec §5a "Classification"); first matching rule wins. Same rules
/// and order as Windows' `HandoffCatalog.Classify`.
enum HandoffCatalog {
    static let apple: UInt16 = 0x004C  // Bluetooth SIG company ID
    static let appleUSB: UInt16 = 0x05AC
    static let microsoft: UInt16 = 0x045E
    static let sony: UInt16 = 0x054C
    static let nintendo: UInt16 = 0x057E
    static let logitech: UInt16 = 0x046D
    static let zmkVendor: UInt16 = 0x1D50
    static let zmkProduct: UInt16 = 0x615E

    static func classify(_ d: DeviceIdentity) -> HandoffAssessment {
        func nameHas(_ words: String...) -> Bool {
            words.contains { d.name.range(of: $0, options: .caseInsensitive) != nil }
        }
        let isApple = d.vendorID == apple || d.vendorID == appleUSB
        let consoleVendor = d.vendorID == microsoft || d.vendorID == sony || d.vendorID == nintendo
        let controller =
            d.kind == .gameController || nameHas("controller", "gamepad", "joy-con", "dualsense", "dualshock")

        if d.vendorID == zmkVendor, d.productID == zmkProduct {
            return .init(
                type: .devicePicksHost, confidence: .known,
                reason: "ZMK keyboard: it keeps a pairing per profile, and its profile key picks the computer.")
        }

        if isApple || nameHas("Magic Trackpad", "Magic Keyboard", "Magic Mouse") {
            if nameHas("Trackpad", "Magic Keyboard", "Magic Mouse") {
                return .init(
                    type: .pairingHandoff, confidence: .known,
                    reason: "Apple Magic accessory: one pairing, and it accepts the next computer once released.")
            }
            if nameHas("AirPods", "Beats") {
                return .init(
                    type: .hostConnects, confidence: .likely,
                    reason: "Apple headphones: they remember several computers and join the one that connects.")
            }
            if isApple {
                return .init(type: .pairingHandoff, confidence: .likely, reason: "Apple accessory.")
            }
        }

        if (controller && consoleVendor)
            || nameHas("Xbox Wireless Controller", "Pro Controller", "DualSense", "DualShock")
        {
            return .init(
                type: .repairByHand, confidence: .known,
                reason: "Game controller: it remembers one Bluetooth computer, and moving it needs its pairing button.")
        }

        if d.vendorID == logitech, d.kind == .input || d.kind == .unknown {
            return .init(
                type: .devicePicksHost, confidence: .likely,
                reason: "Logitech keyboard or mouse: Easy-Switch channels, picked with the button on the device.")
        }

        if d.kind == .audio || nameHas("headphone", "headset", "earbuds", "speaker", "buds") {
            return .init(
                type: .hostConnects, confidence: .likely,
                reason: "Audio device: these usually remember several computers and join the one that connects.")
        }

        if controller {
            return .init(
                type: .repairByHand, confidence: .likely,
                reason: "Game controller: these usually remember one computer and need pairing mode to move.")
        }

        if d.isLowEnergy, d.kind == .input {
            return .init(
                type: .devicePicksHost, confidence: .unsure,
                reason:
                    "Bluetooth LE keyboard or mouse: most of these remember each computer and switch from the device.")
        }

        return .init(type: .unknown, confidence: .unsure, reason: "No details about this device.")
    }
}
