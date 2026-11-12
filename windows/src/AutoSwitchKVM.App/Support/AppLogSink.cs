using AutoSwitchKVM.Core;

namespace AutoSwitchKVM.App.Support;

/// Forwards Core (engine) log entries to the app log: file, debugger output, and the Diagnostics
/// ring buffer.
public sealed class AppLogSink : ILogSink
{
    public void Write(Core.LogLevel level, string category, string message, Exception? exception = null)
    {
        switch (level)
        {
            case Core.LogLevel.Error:
                Log.Error(category, message, exception);
                break;
            case Core.LogLevel.Warn:
                Log.Warn(category, exception is null ? message : $"{message}: {exception.Message}");
                break;
            default:
                Log.Info(category, message);
                break;
        }
    }
}
