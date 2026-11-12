using AutoSwitchKVM.Core;

namespace AutoSwitchKVM.Core.Tests.Fakes;

/// Captures log entries so tests can assert that failures are visible in the log.
public sealed class ListLogSink : ILogSink
{
    public List<(LogLevel Level, string Category, string Message, Exception? Exception)> Entries { get; } = new();

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        lock (Entries) Entries.Add((level, category, message, exception));
    }

    public bool Has(LogLevel level, string fragment)
    {
        lock (Entries)
            return Entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    public string Dump()
    {
        lock (Entries) return string.Join("\n", Entries.Select(e => $"{e.Level} [{e.Category}] {e.Message}"));
    }
}
