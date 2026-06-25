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
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _saveGate = new();
    private Timer? _debounceTimer;
    private string? _pendingJson;

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

    /// Write immediately, replacing the existing config atomically.
    public void Save(AppConfig config)
    {
        CancelDebouncedSave();
        WriteJsonAtomic(Serialize(config));
    }

    /// Schedule an atomic write. Rapid UI edits coalesce into one disk write.
    public void SaveDebounced(AppConfig config, int delayMs = 400)
    {
        var json = Serialize(config);
        lock (_saveGate)
        {
            _pendingJson = json;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => FlushPendingSave(), null, Math.Max(0, delayMs), Timeout.Infinite);
        }
    }

    public void FlushPendingSave()
    {
        string? json;
        lock (_saveGate)
        {
            json = _pendingJson;
            _pendingJson = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        if (json != null)
            WriteJsonAtomic(json);
    }

    private static string Serialize(AppConfig config) =>
        JsonSerializer.Serialize(config.Normalized(), Options);

    private void CancelDebouncedSave()
    {
        lock (_saveGate)
        {
            _pendingJson = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private void WriteJsonAtomic(string json)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, Path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* best-effort cleanup */ }
        }
    }
}