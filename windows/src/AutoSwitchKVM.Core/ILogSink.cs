namespace AutoSwitchKVM.Core;

public enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// Logging seam for Core (which can't reference the app's logger). The app forwards to its
/// file/ring-buffer log; tests capture entries to assert on them.
public interface ILogSink
{
    void Write(LogLevel level, string category, string message, Exception? exception = null);
}

public sealed class NullLogSink : ILogSink
{
    public static readonly NullLogSink Instance = new();
    public void Write(LogLevel level, string category, string message, Exception? exception = null) { }
}
