using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using Xunit;

namespace AutoSwitchKVM.Core.Tests;

/// Ports osx ProfilesTests.swift (active-profile accessors, round-trip, legacy migration).
public class ProfilesTests
{
    [Fact]
    public void Active_accessors_follow_active_profile()
    {
        var cfg = AppConfig.Default();                       // one "Default" profile
        cfg.Source = new USBSource { Name = "Desk", VendorID = 3, ProductIDs = new() { 4 } };

        var travel = new Profile
        {
            Name = "Travel",
            Source = new USBSource { Name = "TravelHub", VendorID = 5, ProductIDs = new() { 6 } },
        };
        cfg.Profiles.Add(travel);

        Assert.Equal("Desk", cfg.Source?.Name);              // Default still active

        cfg.ActiveProfileID = travel.Id;
        Assert.Equal("TravelHub", cfg.Source?.Name);

        // Editing through the accessor writes only the active (Travel) profile.
        cfg.Devices = new List<BTDevice> { new() { Name = "Mouse", Address = "aa-bb" } };
        Assert.Single(cfg.Profiles.First(p => p.Id == travel.Id).Devices);
        Assert.Empty(cfg.Profiles.First().Devices);          // Default untouched
    }

    [Fact]
    public void Round_trip_preserves_profiles()
    {
        var tmp = TempPath();
        try
        {
            var cfg = AppConfig.Default();
            cfg.ActiveProfileName = "Desk";                  // rename the active (Default) profile
            cfg.Source = new USBSource { Name = "Hub", VendorID = 1, ProductIDs = new() { 2 } };
            cfg.Profiles.Add(new Profile { Name = "Travel" });

            var store = new ConfigStore(tmp);
            store.Save(cfg);
            var back = store.Load();

            Assert.Equal(2, back.Profiles.Count);
            Assert.Equal(cfg.ActiveProfileID, back.ActiveProfileID);
            Assert.Equal("Hub", back.Source?.Name);
            Assert.Equal(new[] { "Desk", "Travel" }, back.Profiles.Select(p => p.Name).OrderBy(n => n).ToArray());
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void Legacy_config_migrates_to_default_profile()
    {
        var tmp = TempPath();
        try
        {
            // Pre-profiles config: top-level source/devices, no "profiles".
            File.WriteAllText(tmp, """
            {
              "source": { "name": "Old KVM", "vendorID": 1507, "productIDs": [1574, 1552] },
              "devices": [
                { "id": "11111111-1111-1111-1111-111111111111", "name": "Trackpad",
                  "address": "3c-50-02-bf-22-45", "enabled": true, "managePairing": true }
              ],
              "debounceMs": 900
            }
            """);

            var cfg = new ConfigStore(tmp).Load();

            Assert.Single(cfg.Profiles);
            Assert.Equal("Default", cfg.Profiles[0].Name);
            Assert.Equal(cfg.Profiles[0].Id, cfg.ActiveProfileID);
            Assert.Equal("Old KVM", cfg.Source?.Name);
            Assert.Contains((ushort)1574, cfg.Source!.ProductIDs);
            Assert.Contains((ushort)1552, cfg.Source!.ProductIDs);
            Assert.Single(cfg.Devices);
            Assert.True(cfg.Devices[0].ManagePairing);
            Assert.Equal("3C5002BF2245", cfg.Devices[0].AddressDigits);
            Assert.Equal(900, cfg.DebounceMs);
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
