using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using AutoSwitchKVM.Core.Tests.Fakes;
using Xunit;

namespace AutoSwitchKVM.Core.Tests;

/// Failure classification, logging, manual-action routing, and suspend handling in the engine
/// (SPECIFICATION.md §3 "Failure classification"). Hardware motivation: on the Magic Trackpad,
/// AuthenticationNotAllowed (the Mac still holds the bond) burned the exponential-backoff budget,
/// and failures were invisible in the log.
public class FailureHandlingTests
{
    private const ushort Vendor = 0x05E3;
    private const ushort Product = 0x0626;
    private const string Addr = "3C:50:02:BF:22:45";
    private const string Addr2 = "AA:BB:CC:DD:EE:FF";
    private static readonly string N = Addr.Replace(":", "");

    private static BluetoothException Fail(BluetoothFailureKind kind, string msg = "failed") => new(kind, msg);

    private static SelectionEngine MakeEngine(out FakeBluetoothController bt, out AppConfig config,
        out ListLogSink log, bool managePairing = true, FakeUsbMonitor? usb = null)
    {
        config = AppConfig.Default();
        config.Source = new USBSource { Name = "Hub", VendorID = Vendor, ProductIDs = new() { Product } };
        config.Devices = new List<BTDevice>
        {
            new() { Name = "Trackpad", Address = Addr, Enabled = true, ManagePairing = managePairing },
        };
        bt = new FakeBluetoothController();
        log = new ListLogSink();
        return new SelectionEngine(config, usb ?? new FakeUsbMonitor(), bt, log)
        {
            SleepHook = _ => Task.CompletedTask,
            CallTimeoutOverride = TimeSpan.FromSeconds(5),
        };
    }

    private static async Task Arrive(SelectionEngine engine)
    {
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
    }

    private static async Task Leave(SelectionEngine engine)
    {
        engine.HandleUsb(Vendor, Product, added: false);
        await engine.EvaluateNowAsync();
    }

    private static int Count(FakeBluetoothController bt, string call) => bt.Calls.Count(c => c == $"{call}:{N}");

    // ---- Classification-driven retry policy ----

    [Fact]
    public async Task Busy_failures_retry_without_consuming_attempts()
    {
        var engine = MakeEngine(out var bt, out var config, out _);
        config.ConnectRetryMax = 2;
        for (var i = 0; i < 4; i++) bt.PairErrors.Enqueue(Fail(BluetoothFailureKind.DeviceBusy, "AuthenticationNotAllowed"));

        await Arrive(engine);

        Assert.Equal(DeviceStatus.Connected, engine.Statuses.Values.Single());
        Assert.Equal(5, Count(bt, "pair"));        // 4 refusals + the one that worked, despite retryMax=2
        Assert.Equal(0, Count(bt, "unpair"));      // a refusal is not a stale bond
    }

    [Fact]
    public async Task Busy_budget_exhaustion_falls_back_to_hard_attempts_then_gives_up()
    {
        var engine = MakeEngine(out var bt, out var config, out var log);
        engine.MaxSoftRetries = 2;
        config.ConnectRetryMax = 2;
        for (var i = 0; i < 10; i++) bt.PairErrors.Enqueue(Fail(BluetoothFailureKind.DeviceBusy, "AuthenticationNotAllowed"));

        await Arrive(engine);

        Assert.Equal(4, Count(bt, "pair"));        // 2 soft + 2 hard
        var status = engine.Statuses.Values.Single();
        Assert.Equal(DeviceStatusKind.Error, status.Kind);
        Assert.Contains("gave up", status.Label);
        Assert.Contains("AuthenticationNotAllowed", status.Label);
        Assert.True(log.Has(LogLevel.Warn, "soft-retry budget"), log.Dump());
    }

    [Fact]
    public async Task Operation_in_progress_is_a_soft_retry()
    {
        var engine = MakeEngine(out var bt, out var config, out _);
        config.ConnectRetryMax = 1;
        bt.PairErrors.Enqueue(Fail(BluetoothFailureKind.OperationInProgress, "OperationAlreadyInProgress"));

        await Arrive(engine);

        Assert.Equal(DeviceStatus.Connected, engine.Statuses.Values.Single());
        Assert.Equal(0, Count(bt, "unpair"));
    }

    [Fact]
    public async Task Authentication_failure_repairs_from_scratch()
    {
        var engine = MakeEngine(out var bt, out _, out _);
        bt.PairErrors.Enqueue(Fail(BluetoothFailureKind.AuthenticationFailed, "AuthenticationFailure"));

        await Arrive(engine);

        var firstPair = bt.Calls.IndexOf($"pair:{N}");
        var unpair = bt.Calls.IndexOf($"unpair:{N}");
        var lastPair = bt.Calls.LastIndexOf($"pair:{N}");
        Assert.True(firstPair < unpair && unpair < lastPair, string.Join(", ", bt.Calls));
        Assert.Equal(DeviceStatus.Connected, engine.Statuses.Values.Single());
    }

    [Fact]
    public async Task Device_not_found_retries_without_unpairing()
    {
        var engine = MakeEngine(out var bt, out _, out _);
        bt.PairErrors.Enqueue(Fail(BluetoothFailureKind.DeviceNotFound, "not known"));

        await Arrive(engine);

        Assert.Equal(2, Count(bt, "pair"));
        Assert.Equal(0, Count(bt, "unpair"));
        Assert.Equal(DeviceStatus.Connected, engine.Statuses.Values.Single());
    }

    [Fact]
    public async Task Invalid_address_gives_up_immediately()
    {
        var engine = MakeEngine(out var bt, out _, out _);
        for (var i = 0; i < 10; i++) bt.PairErrors.Enqueue(Fail(BluetoothFailureKind.InvalidAddress, "bad MAC"));

        await Arrive(engine);

        Assert.Equal(1, Count(bt, "pair"));
        Assert.Contains("gave up", engine.Statuses.Values.Single().Label);
    }

    [Fact]
    public async Task Hung_pair_is_classified_as_timeout_and_logged()
    {
        var engine = MakeEngine(out var bt, out var config, out var log);
        engine.CallTimeoutOverride = TimeSpan.FromMilliseconds(50);
        config.ConnectRetryMax = 1;
        bt.PairHangs = true;

        await Arrive(engine);

        Assert.True(log.Has(LogLevel.Warn, "pair failed"), log.Dump());
        Assert.True(log.Has(LogLevel.Warn, "[Timeout]"), log.Dump());
        Assert.True(log.Has(LogLevel.Error, "gave up"), log.Dump());
    }

    [Fact]
    public async Task Timeout_then_retry_repairs_from_scratch()
    {
        var engine = MakeEngine(out var bt, out var config, out _);
        engine.CallTimeoutOverride = TimeSpan.FromMilliseconds(50);
        config.ConnectRetryMax = 2;
        bt.PairHangs = true;
        bt.AfterUnpair = () => bt.PairHangs = false;

        await Arrive(engine);

        Assert.Equal(1, Count(bt, "unpair"));
        Assert.Equal(DeviceStatus.Connected, engine.Statuses.Values.Single());
    }

    /// The source leaves while a pair is in flight: the stale connect run must not go on to
    /// connect (on Windows: pair again) after the release, and must not overwrite the release's
    /// Disconnected status with its own Idle.
    [Fact]
    public async Task Connect_run_stops_after_pair_when_source_left_meanwhile()
    {
        var engine = MakeEngine(out var bt, out _, out _);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = true;
        bt.BeforePair = async () =>
        {
            if (!armed) return;
            armed = false;
            reached.SetResult();
            await release.Task;
        };

        engine.HandleUsb(Vendor, Product, added: true);
        var arrival = engine.EvaluateNowAsync();
        await reached.Task;

        await Leave(engine);   // release runs while the pair is suspended
        release.SetResult();
        await arrival;

        Assert.DoesNotContain($"connect:{N}", bt.Calls);
        Assert.Equal(DeviceStatus.Disconnected, engine.Statuses.Values.Single());
    }

    // ---- Visibility: nothing fails silently ----

    [Fact]
    public async Task Give_up_notice_and_log_carry_the_cause()
    {
        var engine = MakeEngine(out var bt, out var config, out var log);
        config.ConnectRetryMax = 2;
        bt.ConnectError = new BluetoothException("link key rejected");
        string? notice = null;
        engine.OnNotice = (title, body) => { if (title == "Connection failed") notice = body; };

        await Arrive(engine);

        Assert.Contains("link key rejected", notice);
        Assert.True(log.Has(LogLevel.Error, "last error: [Unknown] link key rejected"), log.Dump());
    }

    [Fact]
    public async Task Best_effort_step_failures_are_logged_not_swallowed()
    {
        var engine = MakeEngine(out var bt, out _, out var log);
        bt.DisconnectError = new BluetoothException("radio hiccup");

        await Arrive(engine);

        Assert.Equal(DeviceStatus.Connected, engine.Statuses.Values.Single());  // still connects
        Assert.True(log.Has(LogLevel.Warn, "post-pair disconnect failed"), log.Dump());
        Assert.True(log.Has(LogLevel.Warn, "radio hiccup"), log.Dump());
    }

    [Fact]
    public async Task Status_query_failure_is_a_classified_try_not_a_crash()
    {
        var engine = MakeEngine(out var bt, out var config, out var log, managePairing: false);
        config.ConnectRetryMax = 2;
        bt.IsConnectedError = new InvalidOperationException("boom");

        await Arrive(engine);  // must not throw

        Assert.Contains("gave up", engine.Statuses.Values.Single().Label);
        Assert.True(log.Has(LogLevel.Warn, "InvalidOperationException: boom"), log.Dump());
    }

    [Fact]
    public async Task Background_evaluate_failures_are_logged()
    {
        var engine = MakeEngine(out _, out var config, out var log);
        config.ArrivalDebounceMs = 0;
        engine.Changed += () => throw new InvalidOperationException("ui handler blew up");

        engine.HandleUsb(Vendor, Product, added: true);
        engine.ScheduleEvaluate();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!log.Has(LogLevel.Error, "debounced evaluate failed") && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(log.Has(LogLevel.Error, "debounced evaluate failed"), log.Dump());
    }

    // ---- Release (the direction that strands the other host if it fails) ----

    [Fact]
    public async Task Release_retries_a_failed_unpair_once()
    {
        var engine = MakeEngine(out var bt, out _, out var log);
        await Arrive(engine);
        bt.UnpairErrors.Enqueue(new BluetoothException("transient"));

        await Leave(engine);

        Assert.Equal(2, Count(bt, "unpair"));
        Assert.DoesNotContain(N, bt.Paired);
        Assert.Equal(DeviceStatus.Disconnected, engine.Statuses.Values.Single());
        Assert.True(log.Has(LogLevel.Warn, "retrying once"), log.Dump());
    }

    [Fact]
    public async Task Release_failure_is_reported()
    {
        var engine = MakeEngine(out var bt, out _, out var log);
        await Arrive(engine);
        bt.UnpairErrors.Enqueue(new BluetoothException("stuck"));
        bt.UnpairErrors.Enqueue(new BluetoothException("still stuck"));
        string? title = null;
        engine.OnNotice = (t, _) => title = t;

        await Leave(engine);

        Assert.Equal(DeviceStatusKind.Error, engine.Statuses.Values.Single().Kind);
        Assert.Equal("Release failed", title);
        Assert.True(log.Has(LogLevel.Error, "release FAILED"), log.Dump());
    }

    // ---- Manual actions go through the engine ----

    /// Previously the Settings "Unpair" button bypassed the engine: the late "disconnected" event
    /// looked like an unexpected drop and auto-reconnect re-paired the device the user released.
    [Fact]
    public async Task Manual_unpair_while_selected_does_not_trigger_auto_reconnect()
    {
        var engine = MakeEngine(out var bt, out _, out _);
        engine.AutoReconnectDelayMs = 0;
        engine.RefreshMonitoring();
        await Arrive(engine);
        var device = engine.Config.Devices.Single();
        var pairsBefore = Count(bt, "pair");

        await engine.UnpairDeviceNowAsync(device);
        bt.FireConnectionChange(Addr, connected: false);  // the stack's late notification

        Assert.Equal(pairsBefore, Count(bt, "pair"));
        Assert.Equal(DeviceStatus.Disconnected, engine.Statuses.Values.Single());
    }

    [Fact]
    public async Task Manual_connect_supersedes_in_flight_automation()
    {
        var engine = MakeEngine(out var bt, out var config, out var log, managePairing: false);
        config.Devices.Add(new BTDevice { Name = "Keyboard", Address = Addr2, Enabled = true });

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = true;
        bt.BeforeConnect = async () =>
        {
            if (!armed) return;
            armed = false;
            reached.SetResult();
            await release.Task;
        };

        engine.HandleUsb(Vendor, Product, added: true);
        var arrival = engine.EvaluateNowAsync();
        await reached.Task;  // automation is mid-connect on the trackpad

        await engine.ConnectDeviceNowAsync(config.Devices[0]);
        release.SetResult();
        await arrival;

        Assert.True(log.Has(LogLevel.Info, "supersedes"), log.Dump());
        Assert.DoesNotContain(Addr2.Replace(":", ""), bt.Connected);  // superseded run stopped
    }

    [Fact]
    public async Task Manual_release_releases_one_device()
    {
        var engine = MakeEngine(out var bt, out _, out _);
        await Arrive(engine);

        await engine.ReleaseDeviceNowAsync(engine.Config.Devices.Single());

        Assert.DoesNotContain(N, bt.Paired);
        Assert.Equal(DeviceStatus.Disconnected, engine.Statuses.Values.Single());
    }

    // ---- Suspend / resume ----

    [Fact]
    public async Task Suspend_halts_automation_and_resume_reconnects()
    {
        var usb = new FakeUsbMonitor();
        var engine = MakeEngine(out var bt, out _, out _, usb: usb);

        engine.NotifySuspending();
        await Arrive(engine);
        Assert.Empty(bt.Calls);

        usb.SetDevices(new UsbDeviceInfo(Vendor, Product, "Hub", "USB\\VID_05E3&PID_0626"));
        engine.NotifyResumed();
        await engine.ReevaluateAsync();
        await engine.EvaluateNowAsync();

        Assert.Equal(DeviceStatus.Connected, engine.Statuses.Values.Single());
    }

    [Fact]
    public async Task Suspend_blocks_auto_reconnect()
    {
        var engine = MakeEngine(out var bt, out _, out _, managePairing: false);
        engine.AutoReconnectDelayMs = 0;
        engine.RefreshMonitoring();
        await Arrive(engine);

        engine.NotifySuspending();
        bt.Connected.Remove(N);
        bt.FireConnectionChange(Addr, connected: false);

        Assert.DoesNotContain(N, bt.Connected);
    }
}
