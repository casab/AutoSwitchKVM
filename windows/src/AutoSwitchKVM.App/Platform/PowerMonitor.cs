using Microsoft.Win32;

namespace AutoSwitchKVM.App.Platform;

/// Observes system sleep/wake (mirrors the macOS SleepWakeMonitor). On suspend we proactively
/// disconnect (so another host can take the device while this PC sleeps); on resume we re-seed.
///
/// SystemEvents callbacks arrive on a dedicated SystemEvents thread - the consumer (AppController)
/// marshals OnSuspend/OnResume onto the UI thread before touching the engine.
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
