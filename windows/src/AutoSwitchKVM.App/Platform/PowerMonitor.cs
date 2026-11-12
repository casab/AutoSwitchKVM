using Microsoft.Win32;

namespace AutoSwitchKVM.App.Platform;

/// Observes system sleep/wake and log-off/shutdown (mirrors the macOS SleepWakeMonitor). On suspend
/// and on session end we proactively release devices (so another host can take them while this PC
/// is away); on resume we re-seed.
///
/// SystemEvents raises these on its broadcast thread (delivery requires a pumping message loop
/// somewhere in the process — the WinUI dispatcher provides one). OnSuspend / OnSessionEnding
/// deliberately run (and may block, bounded) on that thread: Windows gives only ~2 s of grace, so
/// the consumer must NOT defer the release to a queue that may never get pumped. OnResume may be
/// marshaled to the UI thread as usual.
public sealed class PowerMonitor : IDisposable
{
    public Action? OnSuspend;
    public Action? OnResume;
    /// Log-off or shutdown (the reason is passed for the log).
    public Action<string>? OnSessionEnding;

    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEndingEvent;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) OnSuspend?.Invoke();
        else if (e.Mode == PowerModes.Resume) OnResume?.Invoke();
    }

    private void OnSessionEndingEvent(object? sender, SessionEndingEventArgs e) =>
        OnSessionEnding?.Invoke(e.Reason == SessionEndReasons.Logoff ? "log-off" : "shutdown");

    public void Dispose()
    {
        if (!_started) return;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEndingEvent;
        _started = false;
    }
}
