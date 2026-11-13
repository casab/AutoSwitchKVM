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
        var engine = Make(out _, out var hs, out _);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        await engine.PollStatusesAsync();

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
    public async Task Device_that_connects_while_switched_away_is_sent_on()
    {
        var engine = Make(out var bt, out var hs, out _);
        await engine.EvaluateNowAsync();                 // source absent: the KVM is on the other computer
        Assert.False(engine.Selected);

        bt.FireConnectionChange(K380, connected: true);  // keyboard woke up on this PC's channel

        Assert.Contains($"switch:{Norm(K380)}:1", hs.Calls);
    }
}
