namespace AutoSwitchKVM.Core;

/// Decides which device status changes the Windows status panel shows (StatusPopup); spec
/// "Switch status panel", same rules as macOS' SwitchStatusModel.
///   - Only after a KVM move: a burst starts when Selected flips (after the engine's first
///     evaluation) and lasts until the panel hides. Launch, wake and profile switches connect
///     everything without the KVM moving, and show nothing.
///   - Worth showing: the start of a connect and where it ends, not bookkeeping (into Idle; from
///     Idle straight to a resting state; a device first seen at rest, e.g. just added).
///   - A device that ended up not connected (or with a problem) while this PC is selected is
///     reported once, then left out (its retries, later switches) until it connects again; that
///     reconnect is shown even outside a burst (user, 2026-11-16: a switched-off speaker kept
///     popping the panel).
public sealed class StatusChangeFilter
{
    private readonly Dictionary<Guid, DeviceStatusKind> _last = new();
    private readonly HashSet<Guid> _quiet = new();
    private bool? _lastSelected;
    private bool _inBurst;

    /// A KVM move started the current burst; the panel's header names where it went.
    public bool InBurst => _inBurst;

    /// The devices whose change since the last call is worth showing. Until the engine has
    /// evaluated presence, and at its first evaluation, this only records.
    public List<Guid> Changed(IEnumerable<(Guid Id, DeviceStatusKind Kind)> statuses, bool selected, bool evaluated)
    {
        var changed = new List<Guid>();
        var list = statuses.ToList();
        if (!evaluated || _lastSelected is not bool previous)
        {
            _lastSelected = evaluated ? selected : null;
            foreach (var (id, kind) in list) _last[id] = kind;
            return changed;
        }
        if (previous != selected)
        {
            _lastSelected = selected;
            _inBurst = true;
        }
        foreach (var (id, kind) in list)
        {
            var had = _last.TryGetValue(id, out var old);
            _last[id] = kind;
            var back = kind == DeviceStatusKind.Connected && _quiet.Remove(id);
            if (!had || !Worth(old, kind)) continue;
            if (!back && (!_inBurst || _quiet.Contains(id))) continue;
            changed.Add(id);
            if (kind is DeviceStatusKind.Error or DeviceStatusKind.BluetoothOff
                || (selected && kind == DeviceStatusKind.Disconnected))
                _quiet.Add(id);
        }
        return changed;
    }

    /// The panel hid: the burst is over.
    public void EndBurst() => _inBurst = false;

    private static bool Worth(DeviceStatusKind from, DeviceStatusKind to)
    {
        if (from == to || to == DeviceStatusKind.Idle) return false;
        if (from == DeviceStatusKind.Idle && to != DeviceStatusKind.Connecting) return false;
        return true;
    }
}

/// How a row in the status panel reads. Calm = not connected, nothing to fix right now (secondary
/// color, normal hold); Problem = red, held longer.
public enum StatusTone { Ok, Busy, Calm, Problem }

public static class StatusPanelText
{
    /// Same wording as macOS' SwitchStatusModel, "PC" for "Mac". A channel switcher that didn't
    /// come over is told to use its key; other devices just say "Not connected".
    public static (string Text, StatusTone Tone) For(DeviceStatus status, bool selected, bool managePairing,
        bool asleep, bool sentAway, bool switchesChannels, bool dropped)
    {
        if (dropped && status.Kind is DeviceStatusKind.Disconnected or DeviceStatusKind.Idle)
            return ("Dropped unexpectedly", StatusTone.Problem);
        return status.Kind switch
        {
            DeviceStatusKind.Connecting => ("Connecting…", StatusTone.Busy),
            DeviceStatusKind.Connected => ("Connected", StatusTone.Ok),
            DeviceStatusKind.BluetoothOff => ("Bluetooth is off", StatusTone.Problem),
            DeviceStatusKind.Error => (string.IsNullOrEmpty(status.Message) ? "Problem" : $"Problem: {status.Message}", StatusTone.Problem),
            // A pairing device's unanswered count outlives the KVM leaving; it's "Released" then.
            _ when asleep && (selected || !managePairing) => ("Asleep: wake it to connect", StatusTone.Calm),
            _ when selected => managePairing ? ("Disconnected", StatusTone.Ok)
                : switchesChannels ? ("Didn't come over: press its channel key", StatusTone.Calm)
                : ("Not connected", StatusTone.Calm),
            _ when managePairing => ("Released", StatusTone.Ok),
            _ => sentAway ? ("Sent to the other computer", StatusTone.Ok) : ("Disconnected", StatusTone.Ok),
        };
    }
}
