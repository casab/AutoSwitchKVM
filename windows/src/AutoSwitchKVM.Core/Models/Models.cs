using System.Text.Json.Serialization;

namespace AutoSwitchKVM.Core.Models;

// Mirrors osx/Sources/AutoSwitchKVM/Models/Models.swift. JSON keys are kept identical to the
// macOS app (camelCase) via the naming policy in ConfigStore, so config concepts line up
// across platforms. See ../SPECIFICATION.md for the platform-neutral contract.

/// The "switcher source": a single USB vendor plus the set of product IDs that appear when this
/// machine is selected on the KVM. The machine is "selected" when ANY of these product IDs is present.
public sealed class USBSource
{
    public string Name { get; set; } = "";
    public ushort VendorID { get; set; }
    public HashSet<ushort> ProductIDs { get; set; } = new();

    /// "0x05E3 : 0x0610, 0x0626"
    [JsonIgnore]
    public string DisplayVidPid
    {
        get
        {
            var pids = string.Join(", ", ProductIDs.OrderBy(p => p).Select(p => $"0x{p:X4}"));
            return $"0x{VendorID:X4} : {pids}";
        }
    }
}

/// A Bluetooth device managed by the app.
public sealed class BTDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// BT MAC address as a string (config stores the human MAC; the platform layer converts to a
    /// 64-bit address at the WinRT boundary). Colon or dash separators both accepted.
    public string Address { get; set; } = "";

    /// Whether this device participates in automatic handoff.
    public bool Enabled { get; set; } = true;

    /// If true: pair before connecting, unpair after disconnecting. On Windows this is effectively
    /// mandatory for Classic-HID devices (Magic Trackpad): the bond is exclusive, so the handoff IS
    /// pair/unpair. See windows/PLAN.md "Milestone 0 - spike findings".
    public bool ManagePairing { get; set; }

    /// Delay (ms) before connecting this device, to stagger a handoff. Connect order is the
    /// position in the profile's device list.
    public int ConnectDelayMs { get; set; }

    /// Colon-less uppercase hex digits, e.g. "3C5002BF2245" (handy for PnP/BTHENUM matching).
    [JsonIgnore]
    public string AddressDigits =>
        Address.Replace(":", "").Replace("-", "").ToUpperInvariant();
}

/// A global keyboard shortcut. Windows carries a virtual-key code + modifier flags + display string.
/// (macOS used Carbon modifiers; Windows uses its own MOD_* flags - the model is intentionally
/// platform-specific here, registered by HotKeyService.)
public sealed class KeyShortcut
{
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;

    public uint KeyCode { get; set; }
    public uint Modifiers { get; set; }
    public string Display { get; set; } = "";

    public static KeyShortcut DefaultPause => new()
    {
        KeyCode = 0x50, // P
        Modifiers = ModControl | ModAlt,
        Display = "Ctrl+Alt+P",
    };

    public static KeyShortcut DefaultConnectAll => new()
    {
        KeyCode = 0x43, // C
        Modifiers = ModControl | ModAlt,
        Display = "Ctrl+Alt+C",
    };

    public static KeyShortcut DefaultDisconnectAll => new()
    {
        KeyCode = 0x44, // D
        Modifiers = ModControl | ModAlt,
        Display = "Ctrl+Alt+D",
    };
}

/// A named configuration: a source plus its managed devices. Global options live on AppConfig.
public sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public USBSource? Source { get; set; }
    public List<BTDevice> Devices { get; set; } = new();
}

/// Whole-app configuration, persisted as JSON. Profiles + which is active, plus app-wide options.
/// Source/Devices are computed accessors onto the active profile so call sites stay simple.
public sealed class AppConfig
{
    public List<Profile> Profiles { get; set; } = new() { new Profile { Name = "Default" } };
    public Guid ActiveProfileID { get; set; }

    // Timing
    public int DebounceMs { get; set; } = 1200;
    public int ArrivalDebounceMs { get; set; } = 400;
    public int ConnectRetryMax { get; set; } = 6;

    /// Base retry interval (seconds). Backoff between attempts is ConnectRetrySecs * 2^(attempt-1).
    public int ConnectRetrySecs { get; set; } = 2;

    /// Per Bluetooth-call timeout. On Windows a pair includes a BR/EDR discovery inquiry (~30s for the
    /// FindAllAsync fallback), so this must comfortably exceed that or the engine cancels its own
    /// discovery mid-flight. Default 45s (vs macOS 15s).
    public int BtCallTimeoutSecs { get; set; } = 45;

    // Behavior
    public bool ShowNotifications { get; set; }
    public bool NotifyUnexpectedDisconnect { get; set; }
    public bool LaunchAtLogin { get; set; }

    /// When true, automatic handoff is suspended (manual actions still work).
    public bool Paused { get; set; }

    /// macOS-only knob, retained for config parity; ignored on Windows.
    public bool DockAutoHide { get; set; }

    // Global shortcuts (off by default).
    public bool GlobalHotkeysEnabled { get; set; }
    public KeyShortcut? HotkeyPause { get; set; } = KeyShortcut.DefaultPause;
    public KeyShortcut? HotkeyConnectAll { get; set; } = KeyShortcut.DefaultConnectAll;
    public KeyShortcut? HotkeyDisconnectAll { get; set; } = KeyShortcut.DefaultDisconnectAll;

    // ---- Active-profile accessors (not serialized) ----

    [JsonIgnore]
    public Profile? ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileID);

    [JsonIgnore]
    public USBSource? Source
    {
        get => ActiveProfile?.Source;
        set { var p = ActiveProfile; if (p != null) p.Source = value; }
    }

    [JsonIgnore]
    public List<BTDevice> Devices
    {
        get => ActiveProfile?.Devices ?? new List<BTDevice>();
        set { var p = ActiveProfile; if (p != null) p.Devices = value; }
    }

    [JsonIgnore]
    public string ActiveProfileName
    {
        get => ActiveProfile?.Name ?? "";
        set { var p = ActiveProfile; if (p != null) p.Name = value; }
    }

    /// Ensure invariants after construction/deserialization: at least one profile, and a valid
    /// active id. Mirrors the macOS init/migration guard.
    public AppConfig Normalized()
    {
        if (Profiles.Count == 0)
            Profiles.Add(new Profile { Name = "Default" });
        if (!Profiles.Any(p => p.Id == ActiveProfileID))
            ActiveProfileID = Profiles[0].Id;
        return this;
    }

    public static AppConfig Default()
    {
        var cfg = new AppConfig();
        cfg.ActiveProfileID = cfg.Profiles[0].Id;
        return cfg;
    }
}
