using Microsoft.Win32;

namespace AutoSwitchKVM.App.Platform;

/// Observes system sleep/wake (mirrors the macOS SleepWakeMonitor). On suspend we proactively
/// release devices (so another host can take them while this PC sleeps); on resume we re-seed.
///
/// SystemEvents raises these on its broadcast thread (delivery requires a pumping message loop
/// somewhere in the process — the WinUI dispatcher provides one). OnSuspend deliberately runs
/// (and may block, bounded) on that thread: Windows gives only ~2 s of suspend grace, so the
/// consumer must NOT defer the release to a queue that may never get pumped. OnResume may be
/// marshaled to the UI thread as usual.
public sealed class PowerMonitor : IDisposable
{
    public Action? OnSuspend;
    public Action? OnResume;

    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) OnSuspend?.Invoke();
        else if (e.Mode == PowerModes.Resume) OnResume?.Invoke();
    }

    public void Dispose()
    {
        if (!_started) return;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _started = false;
    }
}
