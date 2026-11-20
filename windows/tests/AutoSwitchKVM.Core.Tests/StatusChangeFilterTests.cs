using AutoSwitchKVM.Core;
using Xunit;
using K = AutoSwitchKVM.Core.DeviceStatusKind;

namespace AutoSwitchKVM.Core.Tests;

/// Which status changes the Windows status panel shows, and how a row reads (spec "Switch status
/// panel"; macOS SwitchStatusModelTests has the same cases).
public class StatusChangeFilterTests
{
    private static readonly Guid Speaker = Guid.NewGuid();
    private static readonly Guid Trackpad = Guid.NewGuid();

    /// A filter that saw the engine's first evaluation (KVM here) with these statuses.
    private static StatusChangeFilter Primed(bool selected, params (Guid, K)[] statuses)
    {
        var f = new StatusChangeFilter();
        f.Changed(statuses, selected, evaluated: true);
        return f;
    }

    private static List<Guid> Step(StatusChangeFilter f, Guid id, K kind, bool selected) =>
        f.Changed(new[] { (id, kind) }, selected, evaluated: true);

    [Fact]
    public void A_KVM_move_shows_the_connect_and_its_result()
    {
        var f = Primed(false, (Trackpad, K.Disconnected));
        Assert.Equal(new[] { Trackpad }, Step(f, Trackpad, K.Connecting, selected: true));   // the move starts a burst
        Assert.True(f.InBurst);
        Assert.Equal(new[] { Trackpad }, Step(f, Trackpad, K.Connected, selected: true));
    }

    [Fact]
    public void Launch_wake_and_profile_switches_show_nothing()
    {
        var f = new StatusChangeFilter();
        Assert.Empty(f.Changed(new[] { (Trackpad, K.Idle) }, selected: false, evaluated: false));   // before evaluation
        Assert.Empty(f.Changed(new[] { (Trackpad, K.Connecting) }, selected: true, evaluated: true)); // first evaluation: primes
        Assert.Empty(Step(f, Trackpad, K.Connected, selected: true));                                  // launch connect
        Assert.Empty(Step(f, Trackpad, K.Disconnected, selected: true));                               // sleep release
        Assert.Empty(Step(f, Trackpad, K.Connecting, selected: true));                                 // wake: KVM didn't move
    }

    [Fact]
    public void The_burst_ends_when_the_panel_hides()
    {
        var f = Primed(false, (Trackpad, K.Disconnected));
        Step(f, Trackpad, K.Connecting, selected: true);
        Step(f, Trackpad, K.Connected, selected: true);
        f.EndBurst();
        Assert.Empty(Step(f, Trackpad, K.Connecting, selected: true));   // a reconnect without a move
    }

    [Fact]
    public void Bookkeeping_is_not_shown()
    {
        var f = Primed(true, (Trackpad, K.Idle));
        Step(f, Trackpad, K.Idle, selected: false);                              // the move
        Assert.Empty(Step(f, Trackpad, K.Disconnected, selected: false));        // Idle straight to rest
        Assert.Empty(Step(f, Trackpad, K.Idle, selected: false));                // into Idle
        Assert.Empty(Step(f, Guid.NewGuid(), K.Connected, selected: false));     // first seen (just added)
    }

    [Fact]
    public void A_device_that_did_not_connect_is_reported_once_until_it_connects_again()
    {
        // 2026-11-16: a switched-off speaker kept popping the panel (retries, every switch).
        var f = Primed(false, (Speaker, K.Disconnected));
        Assert.Equal(new[] { Speaker }, Step(f, Speaker, K.Connecting, selected: true));
        Assert.Equal(new[] { Speaker }, Step(f, Speaker, K.Error, selected: true));        // reported
        Assert.Empty(Step(f, Speaker, K.Disconnected, selected: true));                      // slow mode
        Step(f, Speaker, K.Disconnected, selected: false);                                   // KVM leaves
        Assert.Empty(Step(f, Speaker, K.Connecting, selected: true));                        // next switch
        Assert.Empty(Step(f, Speaker, K.Disconnected, selected: true));
        f.EndBurst();
        Assert.Equal(new[] { Speaker }, Step(f, Speaker, K.Connected, selected: true));    // it's back, even outside a burst
    }

    [Fact]
    public void A_release_for_the_other_computer_does_not_silence_the_device()
    {
        var f = Primed(true, (Trackpad, K.Connected));
        Assert.Equal(new[] { Trackpad }, Step(f, Trackpad, K.Disconnected, selected: false));   // released
        Assert.Equal(new[] { Trackpad }, Step(f, Trackpad, K.Connecting, selected: true));      // next arrival
    }

    // ---- Row wording ----

    private static (string, StatusTone) Text(DeviceStatus s, bool selected, bool managePairing = false,
        bool asleep = false, bool sent = false, bool switches = false, bool dropped = false) =>
        StatusPanelText.For(s, selected, managePairing, asleep, sent, switches, dropped);

    [Fact]
    public void Rows_say_what_happened()
    {
        Assert.Equal(("Connecting…", StatusTone.Busy), Text(DeviceStatus.Connecting, true));
        Assert.Equal(("Connected", StatusTone.Ok), Text(DeviceStatus.Connected, true));
        Assert.Equal(("Released", StatusTone.Ok), Text(DeviceStatus.Disconnected, false, managePairing: true));
        Assert.Equal(("Sent to the other computer", StatusTone.Ok), Text(DeviceStatus.Disconnected, false, sent: true));
        Assert.Equal(("Disconnected", StatusTone.Ok), Text(DeviceStatus.Disconnected, false));              // a speaker let go
        Assert.Equal(("Asleep: wake it to connect", StatusTone.Calm), Text(DeviceStatus.Disconnected, true, asleep: true));
        Assert.Equal(("Asleep: wake it to connect", StatusTone.Calm), Text(DeviceStatus.Disconnected, true, managePairing: true, asleep: true));
        Assert.Equal(("Released", StatusTone.Ok), Text(DeviceStatus.Disconnected, false, managePairing: true, asleep: true));
        Assert.Equal(("Didn't come over: press its channel key", StatusTone.Calm),
            Text(DeviceStatus.Disconnected, true, switches: true));
        Assert.Equal(("Not connected", StatusTone.Calm), Text(DeviceStatus.Disconnected, true));
        Assert.Equal(("Dropped unexpectedly", StatusTone.Problem), Text(DeviceStatus.Disconnected, true, dropped: true));
        Assert.Equal(("Problem: no response", StatusTone.Problem), Text(DeviceStatus.Error("no response"), true));
    }
}
