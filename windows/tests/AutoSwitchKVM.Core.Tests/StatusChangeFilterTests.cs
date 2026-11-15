using AutoSwitchKVM.Core;
using Xunit;
using K = AutoSwitchKVM.Core.DeviceStatusKind;

namespace AutoSwitchKVM.Core.Tests;

/// Which status changes the Windows status panel shows (StatusChangeFilter).
public class StatusChangeFilterTests
{
    private static readonly Guid Speaker = Guid.NewGuid();
    private static readonly Guid Trackpad = Guid.NewGuid();

    private static StatusChangeFilter Seeded(params (Guid, K)[] statuses)
    {
        var f = new StatusChangeFilter();
        f.Seed(statuses);
        return f;
    }

    [Fact]
    public void A_switch_shows_the_connect_and_its_result()
    {
        var f = Seeded((Trackpad, K.Disconnected));
        Assert.Equal(new[] { Trackpad }, f.Changed(new[] { (Trackpad, K.Connecting) }, selected: true));
        Assert.Equal(new[] { Trackpad }, f.Changed(new[] { (Trackpad, K.Connected) }, selected: true));
    }

    [Fact]
    public void Bookkeeping_is_not_shown()
    {
        var f = Seeded((Trackpad, K.Idle));
        Assert.Empty(f.Changed(new[] { (Trackpad, K.Connected) }, selected: true));   // first poll at launch
        Assert.Empty(f.Changed(new[] { (Trackpad, K.Idle) }, selected: true));        // into Idle
    }

    [Fact]
    public void A_device_that_did_not_connect_is_reported_once_until_it_connects_again()
    {
        // 2026-11-16: a switched-off speaker kept popping the panel (retries, every switch).
        var f = Seeded((Speaker, K.Connected));
        Assert.Equal(new[] { Speaker }, f.Changed(new[] { (Speaker, K.Disconnected) }, selected: true));   // the drop
        Assert.Empty(f.Changed(new[] { (Speaker, K.Connecting) }, selected: true));                          // auto-reconnect
        Assert.Empty(f.Changed(new[] { (Speaker, K.Error) }, selected: true));
        Assert.Empty(f.Changed(new[] { (Speaker, K.Disconnected) }, selected: true));                        // slow mode
        Assert.Empty(f.Changed(new[] { (Speaker, K.Connecting) }, selected: true));                          // next switch
        Assert.Equal(new[] { Speaker }, f.Changed(new[] { (Speaker, K.Connected) }, selected: true));      // it's back
        Assert.Equal(new[] { Speaker }, f.Changed(new[] { (Speaker, K.Disconnected) }, selected: true));   // a new drop
    }

    [Fact]
    public void A_release_for_the_other_computer_does_not_silence_the_device()
    {
        var f = Seeded((Trackpad, K.Connected));
        Assert.Equal(new[] { Trackpad }, f.Changed(new[] { (Trackpad, K.Disconnected) }, selected: false));   // released
        Assert.Equal(new[] { Trackpad }, f.Changed(new[] { (Trackpad, K.Connecting) }, selected: true));      // next arrival
    }
}
