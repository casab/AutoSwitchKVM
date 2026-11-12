namespace AutoSwitchKVM.Core;

/// Log levels, lowest first. Policy (spec §10, same on macOS):
///   Error   — something failed and needs attention (release failed, gave up, crash, config I/O).
///   Warn    — a problem that was recovered from or worked around (a hard connect failure, a fallback).
///   Info    — the story of a handoff and user actions, ~one line per event (default level).
///   Debug   — step detail and diagnostics (per-try steps, waits, pairing internals, USB snapshots,
///             expected refusals while the other host releases). Costly diagnostics only run here.
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// The config string ↔ level mapping (AppConfig.LogLevel: "error" | "warning" | "info" | "debug").
public static class LogLevels
{
    public const string Default = "info";

    public static LogLevel Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warning" or "warn" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,   // "info", missing, or unknown
    };

    public static string ToConfig(LogLevel level) => level switch
    {
        LogLevel.Debug => "debug",
        LogLevel.Warn => "warning",
        LogLevel.Error => "error",
        _ => "info",
    };
}

/// Logging seam for Core (which can't reference the app's logger). The app forwards to its
/// file/ring-buffer log, which applies the configured minimum level; tests capture everything.
public interface ILogSink
{
    void Write(LogLevel level, string category, string message, Exception? exception = null);
}

public sealed class NullLogSink : ILogSink
{
    public static readonly NullLogSink Instance = new();
    public void Write(LogLevel level, string category, string message, Exception? exception = null) { }
}
