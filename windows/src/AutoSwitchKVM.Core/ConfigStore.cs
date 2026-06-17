using System.Text.Json;
using System.Text.Json.Serialization;
using AutoSwitchKVM.Core.Models;

namespace AutoSwitchKVM.Core;

/// Loads/saves AppConfig as JSON at %LOCALAPPDATA%\AutoSwitchKVM\config.json.
///
/// JSON keys match the macOS app (camelCase) for cross-platform config parity. Missing keys fall
/// back to the property initializers in Models.cs (System.Text.Json simply leaves them unset), which
/// gives the same "default-on-missing" backward compatibility the macOS custom decoder provides.
/// A pre-profiles config (top-level "source"/"devices", no "profiles") is migrated into one
/// "Default" profile, matching the macOS Codable.
///
/// TODO (later): atomic write (temp + move), debounced save, and the absent-vs-null hotkey
/// distinction the macOS Codable implements.
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Path { get; }

    public ConfigStore(string? path = null)
    {
        Path = path ?? DefaultPath();
    }

    public static string DefaultPath()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoSwitchKVM");
        return System.IO.Path.Combine(dir, "config.json");
    }

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(Path))
                return AppConfig.Default();
            return FromJson(File.ReadAllText(Path)).Normalized();
        }
        catch
        {
            // Corrupt/unreadable config should not crash startup; fall back to defaults.
            return AppConfig.Default();
        }
    }

    /// Deserialize, migrating a legacy pre-profiles config (top-level source/devices, no profiles)
    /// into a single "Default" profile. Internal so it can be unit-tested directly.
    internal static AppConfig FromJson(string json)
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? AppConfig.Default();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var hasProfiles = root.ValueKind == JsonValueKind.Object
                          && root.TryGetProperty("profiles", out var profiles)
                          && profiles.ValueKind == JsonValueKind.Array
                          && profiles.GetArrayLength() > 0;
        if (!hasProfiles)
        {
            USBSource? source = null;
            var devices = new List<BTDevice>();
            if (root.TryGetProperty("source", out var srcEl) && srcEl.ValueKind == JsonValueKind.Object)
                source = srcEl.Deserialize<USBSource>(Options);
            if (root.TryGetProperty("devices", out var devEl) && devEl.ValueKind == JsonValueKind.Array)
                devices = devEl.Deserialize<List<BTDevice>>(Options) ?? new List<BTDevice>();

            var profile = new Profile { Name = "Default", Source = source, Devices = devices };
            cfg.Profiles = new List<Profile> { profile };
            cfg.ActiveProfileID = profile.Id;
        }
        return cfg;
    }

    public void Save(AppConfig config)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(config.Normalized(), Options);
        File.WriteAllText(Path, json);
    }
}
