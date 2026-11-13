using AutoSwitchKVM.Core;
using Xunit;

namespace AutoSwitchKVM.Core.Tests;

/// Spec §5a classification, against the devices seen on the user's PC on 2026-11-13.
public class HandoffCatalogTests
{
    private static HandoffAssessment C(string name, bool le, ushort? vid, ushort? pid = null, DeviceKind kind = DeviceKind.Unknown) =>
        HandoffCatalog.Classify(new DeviceIdentity(name, le, vid, pid, kind));

    [Fact]
    public void Magic_trackpad_hands_off_by_pairing()
    {
        var a = C("Engin’s Magic Trackpad", false, 0x004C, 0x0324, DeviceKind.Input);
        Assert.Equal((HandoffType.PairingHandoff, HandoffConfidence.Known), (a.Type, a.Confidence));
        Assert.True(a.DefaultManagePairing(isLowEnergy: false));
    }

    [Fact]
    public void Zmk_keyboard_picks_the_computer_itself()
    {
        var a = C("Reviung41", true, 0x1D50, 0x615E, DeviceKind.Input);
        Assert.Equal((HandoffType.DevicePicksHost, HandoffConfidence.Known), (a.Type, a.Confidence));
        Assert.False(a.DefaultManagePairing(isLowEnergy: true));
    }

    [Theory]
    [InlineData("Xbox Wireless Controller", true, (ushort)0x045E)]
    [InlineData("Pro Controller", false, (ushort)0x057E)]
    [InlineData("DualSense Wireless Controller", false, (ushort)0x054C)]
    public void Console_controllers_re_pair_by_hand(string name, bool le, ushort vid)
    {
        var a = C(name, le, vid, kind: DeviceKind.GameController);
        Assert.Equal((HandoffType.RepairByHand, HandoffConfidence.Known), (a.Type, a.Confidence));
        Assert.False(a.DefaultManagePairing(le));
    }

    [Fact]
    public void Logitech_keyboard_is_likely_easy_switch()
    {
        var a = C("Keyboard K380", false, 0x046D, 0xB342, DeviceKind.Input);
        Assert.Equal((HandoffType.DevicePicksHost, HandoffConfidence.Likely), (a.Type, a.Confidence));
    }

    [Fact]
    public void Audio_devices_join_whichever_computer_connects()
    {
        Assert.Equal(HandoffType.HostConnects, C("WH-1000XM5", false, 0x054C, kind: DeviceKind.Audio).Type);
        Assert.Equal(HandoffType.HostConnects, C("AirPods Pro", false, 0x004C, kind: DeviceKind.Audio).Type);
    }

    [Fact]
    public void Unknown_devices_ask_for_a_test_and_keep_the_transport_default()
    {
        var classic = C("Some Mouse", false, null);
        Assert.Equal(HandoffType.Unknown, classic.Type);
        Assert.True(classic.DefaultManagePairing(isLowEnergy: false));
        Assert.False(classic.DefaultManagePairing(isLowEnergy: true));
        Assert.Equal(HandoffType.DevicePicksHost, C("BLE Keyboard", true, null, kind: DeviceKind.Input).Type);
    }

    [Theory]
    [InlineData("0001004C", (ushort)0x004C)]
    [InlineData("02045E", (ushort)0x045E)]
    [InlineData("011D50", (ushort)0x1D50)]
    public void Parses_vendor_from_windows_pnp_ids(string hex, ushort expected) =>
        Assert.Equal(expected, HandoffCatalog.ParseVendor(hex));
}
