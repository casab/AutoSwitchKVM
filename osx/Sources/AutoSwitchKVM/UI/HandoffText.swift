import Foundation

/// User-facing wording for the device handoff types (SPECIFICATION.md §5a), as they apply on
/// macOS. Titles are shared with Windows (`HandoffText.cs`); "what happens" differs where the
/// platforms do: the Mac can disconnect without unpairing, so type 4 is supported here.
enum HandoffText {
    static let learnMoreURL = URL(string: "https://autoswitchkvm.com/devices/")!

    static func title(_ t: HandoffType) -> String {
        switch t {
        case .pairingHandoff: return "Hands off by pairing"
        case .devicePicksHost: return "Picks the computer itself"
        case .repairByHand: return "Re-pairs by hand"
        case .hostConnects: return "Joins whichever computer connects"
        case .channelSwitch: return "Switches channel on request"
        case .unknown: return "Not sure how this device switches"
        }
    }

    /// What AutoSwitch does (or can't do) with this kind of device on the Mac.
    static func whatHappens(_ t: HandoffType) -> String {
        switch t {
        case .pairingHandoff:
            return "AutoSwitch can switch it: it unpairs the device when the KVM leaves this Mac and pairs it "
                + "when the KVM comes back. Keep Manage pairing on."
        case .devicePicksHost:
            return "AutoSwitch can't switch it: the device keeps a pairing for each computer and its own key "
                + "picks one. Switch with that key, or connect it by USB through the KVM. AutoSwitch shows its "
                + "status. Keep Manage pairing off, or the device will need pairing again."
        case .repairByHand:
            return "AutoSwitch can't switch it: it remembers one computer and only moves in pairing mode. Keep "
                + "it on one computer, or connect it by cable through the KVM. AutoSwitch shows its status. "
                + "Keep Manage pairing off."
        case .hostConnects:
            return "AutoSwitch can switch it: it disconnects the device when the KVM leaves this Mac and "
                + "connects it when the KVM comes back. Its pairings stay as they are. Keep Manage pairing off."
        case .channelSwitch:
            return "AutoSwitch can switch it: when the KVM leaves this Mac, it tells the device to go to the "
                + "other computer's channel. Its pairings stay as they are. Keep Manage pairing off."
        case .unknown:
            return "Test it: remove it in this Mac's Bluetooth settings. If your other computer can then pair it "
                + "right away, without pressing anything on the device, it hands off by pairing: turn Manage "
                + "pairing on."
        }
    }

    /// Short label for a confidence, appended to the title ("likely", "probably").
    static func confidence(_ a: HandoffAssessment) -> String {
        guard a.type != .unknown else { return "" }
        switch a.confidence {
        case .known: return ""
        case .likely: return " (likely)"
        case .unsure: return " (probably)"
        }
    }

    static func canSwitch(_ t: HandoffType) -> Bool {
        t == .pairingHandoff || t == .hostConnects || t == .channelSwitch
    }

    static func symbol(_ t: HandoffType) -> String {
        canSwitch(t) ? "checkmark.circle" : t == .unknown ? "questionmark.circle" : "info.circle"
    }

    /// A hint when the Manage pairing setting works against the device's type, or nil.
    static func managePairingHint(_ a: HandoffAssessment, managePairing: Bool) -> String? {
        guard a.type != .unknown else { return nil }
        let recommended = a.defaultManagePairing(isLowEnergy: false)
        guard recommended != managePairing else { return nil }
        if recommended { return "Turn on Manage pairing: this device only moves when this Mac releases its pairing." }
        if a.type == .channelSwitch { return "Turn off Manage pairing to switch its channel instead of unpairing it." }
        return "Turn off Manage pairing: unpairing this device means pairing it again by hand."
    }
}
