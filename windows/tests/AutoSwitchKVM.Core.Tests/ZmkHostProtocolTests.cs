using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using AutoSwitchKVM.Core.Tests.Fakes;
using Xunit;

namespace AutoSwitchKVM.Core.Tests;

/// The AutoSwitch ZMK host channel protocol (firmware/zmk-autoswitch, protocol v1), the composite
/// switcher, and the engine's "never to an unpaired profile" rule.
public class ZmkHostProtocolTests
{
    private static byte[] InfoReply(byte status, byte count, byte active, ushort paired, params byte[] marks)
    {
        var r = new byte[32];
        r[0] = (byte)'A'; r[1] = (byte)'S'; r[2] = 1; r[3] = 0x81; r[4] = status;
        r[5] = count; r[6] = active; r[7] = (byte)(paired & 0xFF); r[8] = (byte)(paired >> 8);
        Array.Copy(marks, 0, r, 9, marks.Length);
        return r;
    }

    [Fact]
    public void Requests_carry_magic_version_command_and_arguments()
    {
        var r = ZmkHostProtocol.Request(ZmkHostProtocol.SetMark, 2, 0xA5);
        Assert.Equal(32, r.Length);
        Assert.Equal(new byte[] { (byte)'A', (byte)'S', 1, 0x03, 2, 0xA5 }, r.Take(6).ToArray());
        Assert.All(r.Skip(6), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Info_reply_parses_profiles_active_paired_and_marks()
    {
        // 5 profiles (like the Reviung41's BT_SEL 0..4), on profile 0; 0 and 1 paired; the Mac
        // marked profile 1.
        var ch = ZmkHostProtocol.ParseInfo(InfoReply(0, 5, 0, 0b00011, 0xA5, 0xA5, 0, 0, 0));
        Assert.NotNull(ch);
        Assert.Equal(5, ch!.Count);
        Assert.Equal(0, ch.Current);
        Assert.Equal(new[] { true, true, false, false, false }, ch.Paired);
        Assert.Equal(1, ch.MarkedOtherChannel);
    }

    [Theory]
    [InlineData(0)]   // wrong magic
    [InlineData(1)]   // wrong version
    [InlineData(2)]   // error status
    [InlineData(3)]   // active out of range
    public void Bad_info_replies_are_rejected(int which)
    {
        var r = InfoReply(0, 5, 0, 1);
        switch (which)
        {
            case 0: r[0] = (byte)'X'; break;
            case 1: r[2] = 2; break;
            case 2: r[4] = ZmkHostProtocol.StatusUnsupported; break;
            case 3: r[6] = 5; break;
        }
        Assert.Null(ZmkHostProtocol.ParseInfo(r));
    }

    [Fact]
    public void Replies_are_matched_to_their_command()
    {
        var r = InfoReply(0, 5, 0, 1);
        Assert.True(ZmkHostProtocol.IsReplyTo(r, ZmkHostProtocol.GetInfo));
        Assert.False(ZmkHostProtocol.IsReplyTo(r, ZmkHostProtocol.Select));
    }

    [Fact]
    public async Task Composite_uses_the_first_backend_that_answers_and_keeps_it()
    {
        var logitech = new FakeHostSwitcher { SupportedAddress = "34:88:5D:49:88:AF" };
        var zmk = new FakeHostSwitcher { SupportedAddress = "E7:99:A2:72:A6:76", Count = 5, Marks = new byte[5] };
        var composite = new CompositeHostSwitcher(logitech, zmk);

        Assert.Equal(5, (await composite.GetChannelsAsync("E7:99:A2:72:A6:76"))!.Count);
        await composite.SwitchToAsync("E7:99:A2:72:A6:76", 1);
        Assert.Contains("switch:E799A272A676:1", zmk.Calls);
        Assert.Empty(logitech.Calls);
        Assert.Null(await composite.GetChannelsAsync("00:11:22:33:44:55"));
        await Assert.ThrowsAsync<BluetoothException>(() => composite.SwitchToAsync("00:11:22:33:44:55", 1));
    }

    [Fact]
    public async Task Never_switches_to_an_unpaired_profile()
    {
        var config = AppConfig.Default();
        config.Source = new USBSource { Name = "Hub", VendorID = 0x05E3, ProductIDs = new() { 0x0626 } };
        config.Devices = new List<BTDevice> { new() { Name = "Reviung41", Address = "E7:99:A2:72:A6:76", Enabled = true, ReleaseToChannel = 3 } };
        var bt = new FakeBluetoothController();
        var hs = new FakeHostSwitcher { SupportedAddress = "E7:99:A2:72:A6:76", Count = 5, Marks = new byte[5], Paired = new[] { true, true, false, false, false } };
        var engine = new SelectionEngine(config, new FakeUsbMonitor(), bt, hostSwitcher: hs) { SleepHook = _ => Task.CompletedTask };

        engine.HandleUsb(0x05E3, 0x0626, added: true);
        await engine.EvaluateNowAsync();
        engine.HandleUsb(0x05E3, 0x0626, added: false);
        await engine.EvaluateNowAsync();

        Assert.DoesNotContain(hs.Calls, c => c.StartsWith("switch"));   // profile 3 is empty
    }
}
