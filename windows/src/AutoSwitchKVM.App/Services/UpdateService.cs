using AutoSwitchKVM.App.Support;
using Microsoft.UI.Dispatching;
using Velopack;
using Velopack.Sources;

namespace AutoSwitchKVM.App.Services;

public enum UpdateState { NotInstalled, Idle, Checking, UpToDate, Downloading, ReadyToRestart, Failed }

/// Automatic updates from GitHub Releases via Velopack. Checks shortly after launch and then every
/// few hours, downloads in the background, and offers "Restart to update" (Settings ▸ General,
/// the tray menu, a notification). A development build (not installed by Velopack) only reports
/// its version. Events are raised on the UI thread.
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/casab/AutoSwitchKVM";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly DispatcherQueue _dispatcher;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;
    private Timer? _timer;
    private int _checking;

    public UpdateState State { get; private set; }
    public string? AvailableVersion { get; private set; }
    public int DownloadPercent { get; private set; }
    public string? LastError { get; private set; }

    /// The running version: Velopack's package version when installed, else the assembly's.
    public string CurrentVersion { get; }

    public event Action? Changed;
    /// Raised once per downloaded update (for a notification).
    public event Action<string>? UpdateReady;

    public UpdateService()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
        }
        catch (Exception ex)
        {
            Log.Warn("update", $"update manager unavailable: {ex.Message}");
        }

        var installed = _manager?.IsInstalled == true;
        CurrentVersion = installed && _manager!.CurrentVersion is { } v
            ? v.ToString()
            : typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "dev";
        State = installed ? UpdateState.Idle : UpdateState.NotInstalled;

        // An update downloaded earlier but not applied yet (e.g. the app was restarted by Windows).
        if (installed && _manager!.UpdatePendingRestart is { } pending)
        {
            AvailableVersion = pending.Version.ToString();
            State = UpdateState.ReadyToRestart;
        }
    }

    public bool IsInstalled => State != UpdateState.NotInstalled;

    public void Start()
    {
        if (!IsInstalled)
        {
            Log.Info("update", $"version {CurrentVersion} (development build: automatic updates off)");
            return;
        }
        Log.Info("update", $"version {CurrentVersion}; checking for updates every {CheckInterval.TotalHours:0} h");
        _timer = new Timer(_ => _dispatcher.Enqueue("update check", () => CheckAsync(userInitiated: false)),
            null, FirstCheckDelay, CheckInterval);
    }

    public void Stop() => _timer?.Dispose();

    /// Check, and download if there is something newer. Safe to call repeatedly.
    public async Task CheckAsync(bool userInitiated)
    {
        if (_manager is null || !IsInstalled || State == UpdateState.ReadyToRestart) return;
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            Set(UpdateState.Checking);
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
            {
                Log.Debug("update", $"up to date ({CurrentVersion})");
                Set(UpdateState.UpToDate);
                return;
            }

            AvailableVersion = info.TargetFullRelease.Version.ToString();
            Log.Info("update", $"update available: {AvailableVersion}; downloading");
            DownloadPercent = 0;
            Set(UpdateState.Downloading);
            await _manager.DownloadUpdatesAsync(info, p => _dispatcher.Enqueue("download progress", () =>
            {
                // Changed refreshes the whole Settings view model: report in 5 % steps.
                if (p / 5 == DownloadPercent / 5) return;
                DownloadPercent = p;
                Changed?.Invoke();
            }));
            _pending = info;
            Log.Info("update", $"update {AvailableVersion} downloaded; ready to restart");
            Set(UpdateState.ReadyToRestart);
            UpdateReady?.Invoke(AvailableVersion);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            // A background check failing (offline, GitHub rate limit) is not worth a warning.
            if (userInitiated) Log.Warn("update", $"update check failed: {ex.Message}");
            else Log.Debug("update", $"update check failed: {ex.Message}");
            Set(UpdateState.Failed);
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// Exit, apply the downloaded update, and start the new version. The caller shuts the app down
    /// cleanly first (config saved, monitors stopped).
    public void ApplyAndRestart()
    {
        if (_manager is null) return;
        var target = _pending?.TargetFullRelease ?? _manager.UpdatePendingRestart;
        if (target is null) return;
        Log.Info("update", $"restarting to apply {target.Version}");
        _manager.ApplyUpdatesAndRestart(target);
    }

    private void Set(UpdateState state)
    {
        State = state;
        Changed?.Invoke();
    }
}
