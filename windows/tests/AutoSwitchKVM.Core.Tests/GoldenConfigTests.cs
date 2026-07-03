using System.Text.Json;
using AutoSwitchKVM.Core;
using Xunit;

namespace AutoSwitchKVM.Core.Tests;

/// Cross-platform config parity guard. testdata/config-golden.json at the repo root is the
/// canonical fixture both platforms' suites decode, round-trip, and key-compare — any key
/// rename or dropped field on either side fails CI. Hotkeys in the fixture are explicit null
/// (cleared): they are the one deliberately platform-specific part of the schema.
public class GoldenConfigTests
{
    private static string GoldenPath =>
        Path.Combine(AppContext.BaseDirectory, "testdata", "config-golden.json");

    [Fact]
    public void Golden_config_decodes_round_trips_and_keeps_key_parity()
    {
        var json = File.ReadAllText(GoldenPath);
        var cfg = ConfigStore.Parse(json);

        Assert.Equal(1, cfg.SchemaVersion);
        Assert.Equal(2, cfg.Profiles.Count);
        Assert.Equal("Desk", cfg.ActiveProfileName);
        Assert.Equal("Desk KVM", cfg.Source?.Name);
        Assert.Equal(0x05E3, cfg.Source!.VendorID);
        Assert.Equal(new HashSet<ushort> { 0x0610, 0x0626 }, cfg.Source.ProductIDs);
        var device = Assert.Single(cfg.Devices);
        Assert.Equal("Magic Trackpad", device.Name);
        Assert.Equal("3c-50-02-bf-22-45", device.Address);
        Assert.True(device.ManagePairing);
        Assert.Equal(250, device.ConnectDelayMs);
        Assert.Equal(900, cfg.DebounceMs);
        Assert.Equal(300, cfg.ArrivalDebounceMs);
        Assert.Equal(4, cfg.ConnectRetryMax);
        Assert.Equal(3, cfg.ConnectRetrySecs);
        Assert.Equal(25, cfg.BtCallTimeoutSecs);
        Assert.True(cfg.ShowNotifications);
        Assert.True(cfg.NotifyUnexpectedDisconnect);
        Assert.True(cfg.LaunchAtLogin);
        Assert.False(cfg.Paused);
        Assert.True(cfg.DockAutoHide);
        Assert.False(cfg.GlobalHotkeysEnabled);
        Assert.Null(cfg.HotkeyPause);  // explicit null = cleared, not defaulted

        // Round-trip: nothing lost or renamed.
        var reencoded = ConfigStore.ToJson(cfg);
        var again = ConfigStore.Parse(reencoded);
        Assert.Equal(25, again.BtCallTimeoutSecs);
        Assert.Equal("3c-50-02-bf-22-45", again.Devices.Single().Address);
        Assert.Equal(cfg.ActiveProfileID, again.ActiveProfileID);
        Assert.Null(again.HotkeyPause);

        // Top-level key parity with the canonical fixture.
        Assert.Equal(TopLevelKeys(json), TopLevelKeys(reencoded));
    }

    private static HashSet<string> TopLevelKeys(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
    }
}
