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

    /// Only the device's current channel (0-based): one request where GetChannelsAsync takes
    /// several. null when it doesn't answer. For the frequent "where is it" check.
    async Task<int?> GetCurrentChannelAsync(string address, CancellationToken ct = default) =>
        (await GetChannelsAsync(address, ct))?.Current;

    /// Send the device to `channel` (0-based). The device leaves this host right after.
    Task SwitchToAsync(string address, int channel, CancellationToken ct = default);

    /// Mark the device's current channel as "AutoSwitch runs here", so the other host's
    /// AutoSwitch can find it when it releases the device. No-op if already marked.
    Task MarkCurrentChannelAsync(string address, CancellationToken ct = default);

    /// The name each channel's host stored on the device (HID++ Hosts Info), null per channel when
    /// unknown; null overall when the device doesn't store names.
    Task<IReadOnlyList<string?>?> GetChannelNamesAsync(string address, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string?>?>(null);
}

/// A device's channels: how many, which one this host is on (0-based), and one mark byte per
/// channel (HID++ "cookies": stored on the device, cleared by the firmware when a channel is
/// paired with a new host).
public sealed record HostChannels(int Count, int Current, IReadOnlyList<byte> Marks)
{
    /// The byte AutoSwitch writes on its own channel.
    public const byte AutoSwitchMark = 0xA5;

    /// Per channel: is it paired with a host? null when the device doesn't say. HID++ devices
    /// with Hosts Info (0x1815) and the ZMK module report it; an unpaired channel is never a
    /// target: the device would just advertise there for a new pairing.
    public IReadOnlyList<bool>? Paired { get; init; }

    /// This host's channel from the names the device stores (HID++ Hosts Info): the one channel
    /// carrying `myName` (several: the one of them carrying the AutoSwitch mark; a K380 kept this
    /// PC's name on an old pairing too); else, when pairing is known, the only paired channel
    /// without a name (the other computers' AutoSwitch named theirs). null when that doesn't
    /// single one out.
    public static int? OwnChannel(IReadOnlyList<string?>? names, IReadOnlyList<bool>? paired, string myName,
                                  IReadOnlyList<byte>? marks = null)
    {
        if (names is null) return null;
        var mine = Enumerable.Range(0, names.Count).Where(i => names[i] is { } n && NameMatches(n, myName)).ToList();
        if (mine.Count == 1) return mine[0];
        if (mine.Count > 1)
        {
            var marked = mine.Where(i => marks is not null && i < marks.Count && marks[i] == AutoSwitchMark).ToList();
            return marked.Count == 1 ? marked[0] : null;
        }
        if (paired is null) return null;
        var unnamed = Enumerable.Range(0, paired.Count).Where(i => paired[i] && (i >= names.Count || names[i] is null)).ToList();
        return unnamed.Count == 1 ? unnamed[0] : null;
    }

    /// A stored name is this computer's: equal (ignoring case: Windows names are case-insensitive),
    /// or a cut-down copy of at least 8 characters (devices cap the length).
    public static bool NameMatches(string stored, string myName) =>
        string.Equals(stored, myName, StringComparison.OrdinalIgnoreCase)
        || (stored.Length >= 8 && myName.StartsWith(stored, StringComparison.OrdinalIgnoreCase));

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

    /// The single other paired channel, when the device reports pairing (ZMK) and exactly one
    /// channel besides this host's is paired; else null.
    public int? OnlyOtherPairedChannel
    {
        get
        {
            if (Paired is not { } paired) return null;
            var others = Enumerable.Range(0, Math.Min(Count, paired.Count))
                .Where(i => i != Current && paired[i]).ToList();
            return others.Count == 1 ? others[0] : null;
        }
    }

    /// Where "automatic" sends the device: the single other channel marked by AutoSwitch; with no
    /// mark at all, the only other paired channel (a ZMK keyboard paired with just these two
    /// computers works before the other computer's AutoSwitch has marked its profile).
    public int? AutomaticTarget
    {
        get
        {
            if (MarkedOtherChannel is int m) return m;
            var anyOtherMarked = Enumerable.Range(0, Math.Min(Count, Marks.Count))
                .Any(i => i != Current && Marks[i] == AutoSwitchMark);
            return anyOtherMarked ? null : OnlyOtherPairedChannel;
        }
    }
}
