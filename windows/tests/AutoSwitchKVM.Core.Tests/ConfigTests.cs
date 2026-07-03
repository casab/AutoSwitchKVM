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
        Assert.Equal(45, cfg.BtCallTimeoutSecs); // Windows default: ~14s pair + other-host-release race headroom
        Assert.False(cfg.GlobalHotkeysEnabled);
        Assert.Equal("Ctrl+Alt+P", cfg.HotkeyPause?.Display);
        Assert.Equal("Ctrl+Alt+C", cfg.HotkeyConnectAll?.Display);
        Assert.Equal("Ctrl+Alt+D", cfg.HotkeyDisconnectAll?.Display);
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
            Assert.Equal(45, loaded.BtCallTimeoutSecs);
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
    public void Hotkey_absent_defaults_but_present_null_stays_cleared()
    {
        var missing = ConfigStore.FromJson("""
        { "profiles": [{ "name": "Default" }] }
        """).Normalized();
        Assert.Equal("Ctrl+Alt+P", missing.HotkeyPause?.Display);
        Assert.Equal("Ctrl+Alt+C", missing.HotkeyConnectAll?.Display);
        Assert.Equal("Ctrl+Alt+D", missing.HotkeyDisconnectAll?.Display);

        var cleared = ConfigStore.FromJson("""
        {
          "profiles": [{ "name": "Default" }],
          "hotkeyPause": null,
          "hotkeyConnectAll": null,
          "hotkeyDisconnectAll": null
        }
        """).Normalized();
        Assert.Null(cleared.HotkeyPause);
        Assert.Null(cleared.HotkeyConnectAll);
        Assert.Null(cleared.HotkeyDisconnectAll);
    }

    [Fact]
    public void Debounced_save_writes_latest_config()
    {
        var tmp = TempPath();
        try
        {
            var store = new ConfigStore(tmp);
            var cfg = AppConfig.Default();
            cfg.Paused = false;
            store.SaveDebounced(cfg, delayMs: 60_000);  // far future: the flush below decides
            cfg.Paused = true;
            store.SaveDebounced(cfg, delayMs: 60_000);

            store.FlushPendingSave();  // deterministic — no wall-clock race with the timer
            Assert.True(new ConfigStore(tmp).Load().Paused);
        }
        finally { Cleanup(tmp); }
    }

    /// A config exported on macOS stores hotkeys as `carbonModifiers` (macOS virtual-key space);
    /// deserializing here yields Modifiers == 0 and a wrong-space key code. It must fall back to
    /// the platform default — never register a bare-key global hotkey like "End".
    [Fact]
    public void Macos_exported_hotkey_falls_back_to_platform_default()
    {
        var cfg = ConfigStore.Parse("""
        {
          "profiles": [
            {"id": "6f9619ff-8b86-d011-b42d-00c04fc964ff", "name": "Default", "devices": []}
          ],
          "activeProfileID": "6f9619ff-8b86-d011-b42d-00c04fc964ff",
          "paused": true,
          "hotkeyPause": {"keyCode": 35, "carbonModifiers": 4352, "display": "^~P"},
          "hotkeyConnectAll": null
        }
        """);

        Assert.True(cfg.Paused);                                              // the import itself succeeds
        Assert.Equal(KeyShortcut.DefaultPause.KeyCode, cfg.HotkeyPause?.KeyCode);  // not the macOS kVK code
        Assert.NotEqual(0u, cfg.HotkeyPause!.Modifiers);                       // never a bare-key hotkey
        Assert.Null(cfg.HotkeyConnectAll);                                     // explicit null stays cleared
    }

    /// Parity with macOS: the schemaVersion stamp must be written and survive a round-trip
    /// (today Windows strips it from configs that came from the Mac).
    [Fact]
    public void SchemaVersion_is_written_and_preserved()
    {
        var tmp = TempPath();
        try
        {
            var store = new ConfigStore(tmp);
            store.Save(AppConfig.Default());
            Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(tmp));
            Assert.Equal(1, store.Load().SchemaVersion);
        }
        finally { Cleanup(tmp); }
    }

    /// A corrupt config falls back to defaults, but the unreadable original must be preserved —
    /// the next save would otherwise silently destroy the user's profiles.
    [Fact]
    public void Corrupt_config_is_backed_up_before_defaulting()
    {
        var tmp = TempPath();
        try
        {
            File.WriteAllText(tmp, "{this is not json");
            var cfg = new ConfigStore(tmp).Load();

            Assert.Single(cfg.Profiles);  // defaults in use
            Assert.True(File.Exists(tmp + ".bad"), "the corrupt original must be backed up for recovery");
        }
        finally
        {
            Cleanup(tmp);
            if (File.Exists(tmp + ".bad")) File.Delete(tmp + ".bad");
        }
    }

    /// Import path: Parse must throw on input that is not an AutoSwitch KVM config, so the UI
    /// can show an error instead of silently replacing the user's config with defaults.
    [Fact]
    public void Parse_rejects_invalid_input_and_accepts_a_real_config()
    {
        Assert.ThrowsAny<Exception>(() => ConfigStore.Parse("not json at all"));
        Assert.ThrowsAny<Exception>(() => ConfigStore.Parse("""{"unrelated": true}"""));

        var cfg = AppConfig.Default();
        cfg.Paused = true;
        var json = System.Text.Json.JsonSerializer.Serialize(cfg, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });
        Assert.True(ConfigStore.Parse(json).Paused);
    }

    /// A debounced flush that grabbed its JSON before a newer immediate Save must not clobber
    /// the newer file contents when it finally writes.
    [Fact]
    public async Task Debounced_flush_does_not_overwrite_newer_save()
    {
        var tmp = TempPath();
        try
        {
            var store = new ConfigStore(tmp);
            var reachedWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var armed = true;
            store.BeforeWrite = () =>
            {
                if (!armed) return;
                armed = false;
                reachedWrite.SetResult();
                release.Task.Wait();
            };

            var oldCfg = AppConfig.Default();
            oldCfg.Paused = false;
            store.SaveDebounced(oldCfg, delayMs: 0);  // flush grabs the stale JSON...
            await reachedWrite.Task;                  // ...and is suspended just before writing

            var newCfg = AppConfig.Default();
            newCfg.Paused = true;
            store.Save(newCfg);                       // newer immediate save lands first

            release.SetResult();
            await Task.Delay(150);                    // let the stale flush finish

            Assert.True(new ConfigStore(tmp).Load().Paused,
                "a stale debounced write must not clobber a newer save");
        }
        finally { Cleanup(tmp); }
    }

    /// A failed disk write (here: the config path is a directory) must be reported via
    /// OnSaveError, never thrown — an exception in the debounce Timer callback kills the process.
    [Fact]
    public void Save_failure_is_reported_not_thrown()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"autoswitchkvm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);  // the config *path* itself is a directory → writes fail
        try
        {
            var store = new ConfigStore(dir);
            Exception? reported = null;
            store.OnSaveError = ex => reported = ex;

            store.Save(AppConfig.Default());                    // must not throw
            store.SaveDebounced(AppConfig.Default(), delayMs: 0);
            store.FlushPendingSave();                           // must not throw

            Assert.NotNull(reported);
        }
        finally { Directory.Delete(dir, recursive: true); }
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
