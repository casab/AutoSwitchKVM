namespace AutoSwitchKVM.Core;

/// Host-initiated channel switching for multi-host devices that support it (SPECIFICATION.md §5a
/// type 5, "Switches channel on request"; Logitech HID++ 2.0 feature 0x1814 "Change Host").
/// Optional platform service: the engine works without it. Only the host the device is currently
/// connected to can talk to it, which suits the release side of a handoff.
public interface IHostSwitcher
{
    /// The device's channels, or null when it doesn't support channel switching or isn't
    /// reachable right now (not connected to this host).
    Task<HostChannels?> GetChannelsAsync(string address, CancellationToken ct = default);

    /// Send the device to `channel` (0-based). The device leaves this host right after.
    Task SwitchToAsync(string address, int channel, CancellationToken ct = default);

    /// Mark the device's current channel as "AutoSwitch runs here", so the other host's
    /// AutoSwitch can find it when it releases the device. No-op if already marked.
    Task MarkCurrentChannelAsync(string address, CancellationToken ct = default);
}

/// A device's channels: how many, which one this host is on (0-based), and one mark byte per
/// channel (HID++ "cookies": stored on the device, cleared by the firmware when a channel is
/// paired with a new host).
public sealed record HostChannels(int Count, int Current, IReadOnlyList<byte> Marks)
{
    /// The byte AutoSwitch writes on its own channel.
    public const byte AutoSwitchMark = 0xA5;

    /// Per channel: is it paired with a host? null when the protocol doesn't say (Logitech's
    /// Change Host). The ZMK module reports it, and an unpaired profile is never a target: the
    /// keyboard would just advertise there for a new pairing.
    public IReadOnlyList<bool>? Paired { get; init; }

    /// The single other channel marked by AutoSwitch, or null if none (or several: ambiguous).
    public int? MarkedOtherChannel
    {
        get
        {
            var marked = Enumerable.Range(0, Math.Min(Count, Marks.Count))
                .Where(i => i != Current && Marks[i] == AutoSwitchMark).ToList();
            return marked.Count == 1 ? marked[0] : null;
        }
    }
}
