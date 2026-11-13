using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using AutoSwitchKVM.Core.Tests.Fakes;
using Xunit;

namespace AutoSwitchKVM.Core.Tests;

/// Spec §5a type 5: a device that switches channel on request (Logitech HID++ Change Host) is sent
/// to the other computer's channel on release instead of being unpaired.
public class ChannelSwitchTests
{
    private const ushort Vendor = 0x05E3;
    private const ushort Product = 0x0626;
    private const string K380 = "34:88:5D:49:88:AF";
    private static string Norm(string a) => a.Replace(":", "").Replace("-", "").ToUpperInvariant();

    private static SelectionEngine Make(out FakeBluetoothController bt, out FakeHostSwitcher hs, out BTDevice dev,
                                        int? releaseTo = 2, bool managePairing = false)
    {
        var config = AppConfig.Default();
        config.Source = new USBSource { Name = "Hub", VendorID = Vendor, ProductIDs = new() { Product } };
        dev = new BTDevice { Name = "K380", Address = K380, Enabled = true, ManagePairing = managePairing, ReleaseToChannel = releaseTo };
        config.Devices = new List<BTDevice> { dev };
        bt = new FakeBluetoothController();
        hs = new FakeHostSwitcher { SupportedAddress = K380 };
        var engine = new SelectionEngine(config, new FakeUsbMonitor(), bt, hostSwitcher: hs) { SleepHook = _ => Task.CompletedTask };
        engine.RefreshMonitoring();
        return engine;
    }

    private static async Task ArriveThenLeave(SelectionEngine engine)
    {
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        engine.HandleUsb(Vendor, Product, added: false);
        await engine.EvaluateNowAsync();
    }

    [Fact]
    public async Task Release_sends_the_device_to_the_configured_channel_without_unpairing()
    {
        var engine = Make(out var bt, out var hs, out _, releaseTo: 2);
        await ArriveThenLeave(engine);

        Assert.Contains($"switch:{Norm(K380)}:1", hs.Calls);   // channel 2 = index 1
        Assert.DoesNotContain(bt.Calls, c => c.StartsWith("unpair"));
        Assert.DoesNotContain(bt.Calls, c => c.StartsWith("disconnect"));
    }

    [Fact]
    public async Task Connected_while_selected_marks_this_computers_channel_once()
    {
        var engine = Make(out var bt, out var hs, out _);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        bt.Connected.Add(Norm(K380));                    // the other computer sent it over
        bt.FireConnectionChange(K380, connected: true);
        bt.FireConnectionChange(K380, connected: false);
        bt.FireConnectionChange(K380, connected: true);  // reconnects don't mark again

        Assert.Single(hs.Calls, c => c.StartsWith("mark:"));
        Assert.Equal(HostChannels.AutoSwitchMark, hs.Marks[0]);
    }

    [Fact]
    public async Task Automatic_target_is_the_channel_the_other_computer_marked()
    {
        var engine = Make(out _, out var hs, out _, releaseTo: null);
        hs.Marks[2] = HostChannels.AutoSwitchMark;   // the other computer's AutoSwitch is on channel 3
        await ArriveThenLeave(engine);

        Assert.Contains($"switch:{Norm(K380)}:2", hs.Calls);
    }

    [Fact]
    public async Task Unknown_target_falls_back_to_the_usual_release()
    {
        var engine = Make(out var bt, out var hs, out _, releaseTo: null);
        await ArriveThenLeave(engine);

        Assert.DoesNotContain(hs.Calls, c => c.StartsWith("switch"));
        Assert.Contains($"disconnect:{Norm(K380)}", bt.Calls);
    }

    [Fact]
    public async Task Device_without_channel_switching_is_released_as_usual()
    {
        var engine = Make(out var bt, out var hs, out _);
        hs.SupportedAddress = null;
        await ArriveThenLeave(engine);

        Assert.DoesNotContain(hs.Calls, c => c.StartsWith("switch"));
        Assert.Contains($"disconnect:{Norm(K380)}", bt.Calls);
    }

    [Fact]
    public async Task Managed_pairing_devices_are_never_channel_switched()
    {
        var engine = Make(out var bt, out var hs, out _, managePairing: true);
        await ArriveThenLeave(engine);

        Assert.DoesNotContain(hs.Calls, c => c.StartsWith("switch"));
        Assert.Contains($"unpair:{Norm(K380)}", bt.Calls);
    }

    [Fact]
    public async Task Release_stops_when_the_KVM_comes_back()
    {
        var engine = Make(out var bt, out var hs, out _);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        engine.HandleUsb(Vendor, Product, added: false);
        hs.OnGetChannels = () => engine.HandleUsb(Vendor, Product, added: true);   // a fast switch-back
        await engine.EvaluateNowAsync();

        Assert.DoesNotContain(hs.Calls, c => c.StartsWith("switch"));
        Assert.DoesNotContain(bt.Calls, c => c.StartsWith("disconnect") || c.StartsWith("unpair"));
    }

    [Fact]
    public async Task Channel_switches_are_released_before_slow_unpairs()
    {
        var config = AppConfig.Default();
        config.Source = new USBSource { Name = "Hub", VendorID = Vendor, ProductIDs = new() { Product } };
        config.Devices = new List<BTDevice>
        {
            new() { Name = "Trackpad", Address = "3C:50:02:BF:22:45", Enabled = true, ManagePairing = true },   // listed first
            new() { Name = "K380", Address = K380, Enabled = true, ReleaseToChannel = 2 },
        };
        var bt = new FakeBluetoothController();
        var hs = new FakeHostSwitcher { SupportedAddress = K380, SharedLog = bt.Calls };
        var engine = new SelectionEngine(config, new FakeUsbMonitor(), bt, hostSwitcher: hs) { SleepHook = _ => Task.CompletedTask };
        await ArriveThenLeave(engine);

        var sw = bt.Calls.IndexOf($"switch:{Norm(K380)}:1");
        var unpair = bt.Calls.IndexOf("unpair:3C5002BF2245");
        Assert.True(sw >= 0 && unpair >= 0 && sw < unpair, string.Join(", ", bt.Calls));
    }

    [Fact]
    public async Task Known_channel_switcher_is_waited_for_not_connected()
    {
        var engine = Make(out var bt, out _, out var dev);
        var polls = 0;
        engine.SleepHook = _ => { if (++polls == 3) bt.Connected.Add(Norm(K380)); return Task.CompletedTask; };
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();

        Assert.Equal(DeviceStatus.Connected, engine.Statuses[dev.Id]);
        Assert.DoesNotContain(bt.Calls, c => c.EndsWith(Norm(K380)));   // no connect/pair calls
    }

    [Fact]
    public async Task Channel_switcher_that_never_arrives_is_disconnected_not_an_error()
    {
        var engine = Make(out var bt, out _, out var dev);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();

        Assert.Equal(DeviceStatus.Disconnected, engine.Statuses[dev.Id]);
        Assert.DoesNotContain(bt.Calls, c => c.EndsWith(Norm(K380)));
    }

    [Fact]
    public async Task Late_release_waits_for_a_busy_device_instead_of_giving_up()
    {
        var engine = Make(out var bt, out var hs, out var dev);
        await engine.EvaluateNowAsync();                 // switched away
        engine.MarkBusyForTest(dev.Id);                  // a run is still busy with it (fast bounce)

        bt.Connected.Add(Norm(K380));
        bt.FireConnectionChange(K380, connected: true);  // lands here while busy: remembered
        Assert.DoesNotContain(hs.Calls, c => c.StartsWith("switch"));

        engine.ClearBusyForTest(dev.Id);                 // free: still here and still away -> sent on
        Assert.Contains($"switch:{Norm(K380)}:1", hs.Calls);
    }

    [Fact]
    public async Task Late_release_keeps_asking_until_the_device_answers()
    {
        var engine = Make(out var bt, out var hs, out _);
        var polls = 0;
        engine.SleepHook = _ => { if (++polls == 2) hs.Reachable = true; return Task.CompletedTask; };
        await engine.EvaluateNowAsync();                 // switched away
        hs.Reachable = false;                            // its control channel isn't listed yet

        bt.Connected.Add(Norm(K380));
        bt.FireConnectionChange(K380, connected: true);

        Assert.Contains($"switch:{Norm(K380)}:1", hs.Calls);
    }

    [Fact]
    public async Task Late_release_gives_up_quietly_for_devices_that_do_not_switch_channels()
    {
        var engine = Make(out var bt, out var hs, out _, releaseTo: null);   // not a known channel switcher
        hs.SupportedAddress = null;                                          // e.g. a ZMK keyboard
        await engine.EvaluateNowAsync();

        bt.Connected.Add(Norm(K380));
        bt.FireConnectionChange(K380, connected: true);

        Assert.DoesNotContain(hs.Calls, c => c.StartsWith("switch"));
    }

    [Fact]
    public async Task Device_that_connects_while_switched_away_is_sent_on()
    {
        var engine = Make(out var bt, out var hs, out _);
        await engine.EvaluateNowAsync();                 // source absent: the KVM is on the other computer
        Assert.False(engine.Selected);

        bt.Connected.Add(Norm(K380));
        bt.FireConnectionChange(K380, connected: true);  // keyboard woke up on this PC's channel

        Assert.Contains($"switch:{Norm(K380)}:1", hs.Calls);
    }
}
