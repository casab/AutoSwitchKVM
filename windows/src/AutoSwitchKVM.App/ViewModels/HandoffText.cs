using AutoSwitchKVM.Core;

namespace AutoSwitchKVM.App.ViewModels;

/// User-facing wording for the four handoff types (SPECIFICATION.md §5a), as they apply on Windows.
public static class HandoffText
{
    public const string LearnMoreUrl = "https://autoswitchkvm.com/devices/";

    public static string Title(HandoffType t) => t switch
    {
        HandoffType.PairingHandoff => "Hands off by pairing",
        HandoffType.DevicePicksHost => "Picks the computer itself",
        HandoffType.RepairByHand => "Re-pairs by hand",
        HandoffType.HostConnects => "Joins whichever computer connects",
        HandoffType.ChannelSwitch => "Switches channel on request",
        _ => "Not sure how this device switches",
    };

    /// What AutoSwitch does (or can't do) with this kind of device on Windows.
    public static string WhatHappens(HandoffType t) => t switch
    {
        HandoffType.PairingHandoff =>
            "AutoSwitch can switch it: it unpairs the device when the KVM leaves this PC and pairs it when the KVM comes back. Keep Manage pairing on.",
        HandoffType.DevicePicksHost =>
            "AutoSwitch can't switch it: the device keeps a pairing for each computer and its own key picks one. Switch with that key, or connect it by USB through the KVM. AutoSwitch shows its status. Keep Manage pairing off, or the device will need pairing again.",
        HandoffType.RepairByHand =>
            "AutoSwitch can't switch it: it remembers one computer and only moves in pairing mode. Keep it on one computer, connect it by cable through the KVM, or pair it to a Bluetooth-to-USB adapter (for controllers, e.g. 8BitDo) plugged into the KVM. AutoSwitch shows its status. Keep Manage pairing off.",
        HandoffType.HostConnects =>
            "Not on Windows yet: Windows can't disconnect a Bluetooth device without unpairing it. The Mac app can switch these. AutoSwitch shows its status here.",
        HandoffType.ChannelSwitch =>
            "AutoSwitch can switch it: when the KVM leaves this PC, it tells the device to go to the other computer's channel. Its pairings stay as they are. Keep Manage pairing off.",
        _ =>
            "Test it: remove it in this PC's Bluetooth settings. If your other computer can then pair it right away, without pressing anything on the device, it hands off by pairing: turn Manage pairing on.",
    };

    /// Short label for a confidence, appended to the title ("likely", "not sure").
    public static string Confidence(HandoffAssessment a) => a.Type == HandoffType.Unknown ? "" : a.Confidence switch
    {
        HandoffConfidence.Known => "",
        HandoffConfidence.Likely => " (likely)",
        _ => " (probably)",
    };

    /// Segoe Fluent glyph: check for a supported handoff, info otherwise.
    public static string Glyph(HandoffType t) => CanSwitch(t) ? "" : t == HandoffType.Unknown ? "" : "";

    public static bool CanSwitch(HandoffType t) => t is HandoffType.PairingHandoff or HandoffType.ChannelSwitch;
}
