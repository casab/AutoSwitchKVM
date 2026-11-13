namespace AutoSwitchKVM.Core;

/// How a Bluetooth device handles more than one computer (SPECIFICATION.md §5a). Only some types
/// can be switched by a host.
public enum HandoffType
{
    /// Not enough information: ask the user to test it.
    Unknown,
    /// 1. One pairing; once the current host removes it, the next host can pair (Magic Trackpad).
    PairingHandoff,
    /// 2. A pairing per host slot; a key on the device chooses the host (ZMK, Logitech Easy-Switch).
    DevicePicksHost,
    /// 3. Remembers one Bluetooth host; changing hosts needs pairing mode (game controllers).
    RepairByHand,
    /// 4. Remembers several hosts and joins whichever one connects (many headphones).
    HostConnects,
}

public enum HandoffConfidence { Unsure, Likely, Known }

/// What kind of device it is, from the class of device (Classic) or appearance (LE).
public enum DeviceKind { Unknown, Input, GameController, Audio }

/// What a host can learn about a device without talking to it. VendorId/ProductId come from the
/// Device ID record (Classic) or the Device Information service (LE); null when Windows (or
/// macOS) doesn't expose them.
public sealed record DeviceIdentity(
    string Name,
    bool IsLowEnergy,
    ushort? VendorId = null,
    ushort? ProductId = null,
    DeviceKind Kind = DeviceKind.Unknown);

public readonly record struct HandoffAssessment(HandoffType Type, HandoffConfidence Confidence, string Reason)
{
    /// The Manage pairing default when the device is added (spec §5a): on for type 1, and for an
    /// unknown Classic device (the Magic Trackpad case); off for everything else.
    public bool DefaultManagePairing(bool isLowEnergy) =>
        Type == HandoffType.PairingHandoff || (Type == HandoffType.Unknown && !isLowEnergy);
}

/// Best-effort classification (spec §5a "Classification"); first matching rule wins.
public static class HandoffCatalog
{
    public const ushort Apple = 0x004C;        // Bluetooth SIG company ID
    public const ushort AppleUsb = 0x05AC;
    public const ushort Microsoft = 0x045E;
    public const ushort Sony = 0x054C;
    public const ushort Nintendo = 0x057E;
    public const ushort Logitech = 0x046D;
    public const ushort ZmkVendor = 0x1D50;
    public const ushort ZmkProduct = 0x615E;

    public static HandoffAssessment Classify(DeviceIdentity d)
    {
        var name = d.Name ?? "";
        bool NameHas(params string[] words) => words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
        var controller = d.Kind == DeviceKind.GameController || NameHas("controller", "gamepad", "joy-con", "dualsense", "dualshock");

        if (d.VendorId == ZmkVendor && d.ProductId == ZmkProduct)
            return new(HandoffType.DevicePicksHost, HandoffConfidence.Known,
                "ZMK keyboard: it keeps a pairing per profile, and its profile key picks the computer.");

        if (d.VendorId is Apple or AppleUsb || NameHas("Magic Trackpad", "Magic Keyboard", "Magic Mouse"))
        {
            if (NameHas("Trackpad", "Magic Keyboard", "Magic Mouse"))
                return new(HandoffType.PairingHandoff, HandoffConfidence.Known,
                    "Apple Magic accessory: one pairing, and it accepts the next computer once released.");
            if (NameHas("AirPods", "Beats"))
                return new(HandoffType.HostConnects, HandoffConfidence.Likely,
                    "Apple headphones: they remember several computers and join the one that connects.");
            if (d.VendorId is Apple or AppleUsb)
                return new(HandoffType.PairingHandoff, HandoffConfidence.Likely, "Apple accessory.");
        }

        if (controller && d.VendorId is Microsoft or Sony or Nintendo || NameHas("Xbox Wireless Controller", "Pro Controller", "DualSense", "DualShock"))
            return new(HandoffType.RepairByHand, HandoffConfidence.Known,
                "Game controller: it remembers one Bluetooth computer, and moving it needs its pairing button.");

        if (d.VendorId == Logitech && d.Kind is DeviceKind.Input or DeviceKind.Unknown)
            return new(HandoffType.DevicePicksHost, HandoffConfidence.Likely,
                "Logitech keyboard or mouse: Easy-Switch channels, picked with the button on the device.");

        if (d.Kind == DeviceKind.Audio || NameHas("headphone", "headset", "earbuds", "speaker", "buds"))
            return new(HandoffType.HostConnects, HandoffConfidence.Likely,
                "Audio device: these usually remember several computers and join the one that connects.");

        if (controller)
            return new(HandoffType.RepairByHand, HandoffConfidence.Likely,
                "Game controller: these usually remember one computer and need pairing mode to move.");

        if (d.IsLowEnergy && d.Kind == DeviceKind.Input)
            return new(HandoffType.DevicePicksHost, HandoffConfidence.Unsure,
                "Bluetooth LE keyboard or mouse: most of these remember each computer and switch from the device.");

        return new(HandoffType.Unknown, HandoffConfidence.Unsure, "No details about this device.");
    }

    /// The vendor ID from a Bluetooth vendor field as Windows writes it in PnP IDs:
    /// "0001004C" (Classic: 4-digit source + vendor) or "02045E" (LE: 2-digit source + vendor).
    public static ushort? ParseVendor(string? hex) =>
        hex is { Length: >= 4 } && ushort.TryParse(hex[^4..], System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : null;
}
