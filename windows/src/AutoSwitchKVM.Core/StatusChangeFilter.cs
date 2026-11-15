namespace AutoSwitchKVM.Core;

/// Decides which device status changes the Windows status panel shows (StatusPopup). Worth
/// showing: the start of a connect and where it ends, not quiet bookkeeping (into Idle; from
/// Idle straight to a resting state, e.g. the first status poll at launch). A device that ended
/// up not connected (or with a problem) while this PC is selected is reported once, then left
/// out (its retries, the next switches) until it connects again (user, 2026-11-16: a
/// switched-off speaker kept popping the panel).
public sealed class StatusChangeFilter
{
    private readonly Dictionary<Guid, DeviceStatusKind> _last = new();
    private readonly HashSet<Guid> _quiet = new();

    /// Record the current statuses without reporting anything (at startup).
    public void Seed(IEnumerable<(Guid Id, DeviceStatusKind Kind)> statuses)
    {
        foreach (var (id, kind) in statuses) _last[id] = kind;
    }

    /// The devices whose change since the last call is worth showing.
    public List<Guid> Changed(IEnumerable<(Guid Id, DeviceStatusKind Kind)> statuses, bool selected)
    {
        var changed = new List<Guid>();
        foreach (var (id, kind) in statuses)
        {
            var had = _last.TryGetValue(id, out var previous);
            _last[id] = kind;
            if (kind == DeviceStatusKind.Connected) _quiet.Remove(id);
            if (!had || !Worth(previous, kind) || _quiet.Contains(id)) continue;
            changed.Add(id);
            if (kind == DeviceStatusKind.Error || (selected && kind == DeviceStatusKind.Disconnected))
                _quiet.Add(id);
        }
        return changed;
    }

    private static bool Worth(DeviceStatusKind from, DeviceStatusKind to)
    {
        if (from == to || to == DeviceStatusKind.Idle) return false;
        if (from == DeviceStatusKind.Idle && to != DeviceStatusKind.Connecting) return false;
        return true;
    }
}
