using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using Xunit;

namespace AutoSwitchKVM.Core.Tests;

public class ConfigTests
{
    private const string Addr = "3C:50:02:BF:22:45";

    [Fact]
    public void Default_has_one_active_profile()
    {
        var cfg = AppConfig.Default();
        Assert.Single(cfg.Profiles);
        Assert.Equal(cfg.Profiles[0].Id, cfg.ActiveProfileID);
        Assert.Equal("Default", cfg.ActiveProfileName);
    }

    [Fact]
    public void Defaults_match_spec()
    {
        var cfg = new AppConfig();
        Assert.Equal(1200, cfg.DebounceMs);
        Assert.Equal(400, cfg.ArrivalDebounceMs);
        Assert.Equal(6, cfg.ConnectRetryMax);
        Assert.Equal(2, cfg.ConnectRetrySecs);
        Assert.Equal(30, cfg.BtCallTimeoutSecs); // higher than macOS (pair ~14s on Windows)
        Assert.False(cfg.GlobalHotkeysEnabled);
        Assert.False(cfg.Paused);
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        var tmp = TempPath();
        try
        {
            var store = new ConfigStore(tmp);
            var cfg = AppConfig.Default();
            cfg.Source = new USBSource { Name = "KVM", VendorID = 0x05E3, ProductIDs = new() { 0x0626 } };
            cfg.Devices = new List<BTDevice>
            {
                new() { Name = "Trackpad", Address = "3C:50:02:BF:22:45", ManagePairing = true }
            };
            store.Save(cfg);

            var loaded = store.Load();
            Assert.Equal("KVM", loaded.Source!.Name);
            Assert.Equal((ushort)0x05E3, loaded.Source!.VendorID);
            Assert.Contains((ushort)0x0626, loaded.Source!.ProductIDs);
            Assert.Single(loaded.Devices);
            Assert.True(loaded.Devices[0].ManagePairing);
            Assert.Equal("3C5002BF2245", loaded.Devices[0].AddressDigits);
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void Missing_keys_fall_back_to_defaults()
    {
        var tmp = TempPath();
        try
        {
            // Minimal config: one profile, no timing keys present.
            File.WriteAllText(tmp, "{\"profiles\":[{\"name\":\"Default\"}]}");
            var loaded = new ConfigStore(tmp).Load();
            Assert.Equal(1200, loaded.DebounceMs);       // default-on-missing
            Assert.Equal(30, loaded.BtCallTimeoutSecs);
            Assert.Single(loaded.Profiles);
            Assert.Equal("Default", loaded.ActiveProfileName);
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void Load_missing_file_returns_default()
    {
        var loaded = new ConfigStore(TempPath()).Load();
        Assert.Single(loaded.Profiles);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_default()
    {
        var tmp = TempPath();
        try
        {
            File.WriteAllText(tmp, "{ this is not valid json ]");
            var loaded = new ConfigStore(tmp).Load();
            Assert.Single(loaded.Profiles);            // defaults, no crash
            Assert.Equal(1200, loaded.DebounceMs);
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void Saved_json_uses_camelcase_keys_matching_macos()
    {
        var tmp = TempPath();
        try
        {
            var cfg = AppConfig.Default();
            cfg.Source = new USBSource { Name = "Hub", VendorID = 0x05E3, ProductIDs = new() { 0x0626 } };
            cfg.Devices = new List<BTDevice> { new() { Name = "Trackpad", Address = Addr, ManagePairing = true } };
            new ConfigStore(tmp).Save(cfg);

            var json = File.ReadAllText(tmp);
            // Cross-platform config parity: keys must match the macOS Swift property names.
            foreach (var key in new[]
            {
                "\"profiles\"", "\"activeProfileID\"", "\"vendorID\"", "\"productIDs\"",
                "\"managePairing\"", "\"connectDelayMs\"", "\"debounceMs\"", "\"btCallTimeoutSecs\"",
            })
            {
                Assert.Contains(key, json);
            }
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void DockAutoHide_and_paused_persist()
    {
        var tmp = TempPath();
        try
        {
            var cfg = AppConfig.Default();
            cfg.DockAutoHide = true;
            cfg.Paused = true;
            var store = new ConfigStore(tmp);
            store.Save(cfg);

            var back = store.Load();
            Assert.True(back.DockAutoHide);
            Assert.True(back.Paused);
        }
        finally { Cleanup(tmp); }
    }

    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"autoswitchkvm-test-{Guid.NewGuid():N}.json");

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
