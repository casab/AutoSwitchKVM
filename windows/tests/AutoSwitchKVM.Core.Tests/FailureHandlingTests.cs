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

    /// Seen on hardware (2026-11-12): after one hard failure, every following busy retry repeated
    /// the stale-bond unpair. It should run once per hard failure.
    [Fact]
    public async Task Stale_bond_unpair_runs_once_across_soft_retries()
    {
        var engine = MakeEngine(out var bt, out _, out _);
        bt.PairErrors.Enqueue(Fail(BluetoothFailureKind.AuthenticationFailed, "AuthenticationFailure"));
        bt.PairErrors.Enqueue(Fail(BluetoothFailureKind.DeviceBusy, "AuthenticationNotAllowed"));
        bt.PairErrors.Enqueue(Fail(BluetoothFailureKind.DeviceBusy, "AuthenticationNotAllowed"));

        await Arrive(engine);

        Assert.Equal(1, Count(bt, "unpair"));
        Assert.Equal(4, Count(bt, "pair"));
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

    /// Spec §3 hidNotReady: hard, no stale-bond re-pair, and 2 of them end the run early with a
    /// power-cycle hint instead of minutes of pair/unpair churn on a wedged device.
    [Fact]
    public async Task Hid_not_ready_twice_ends_the_run_with_power_cycle_hint()
    {
        var engine = MakeEngine(out var bt, out var config, out var log);
        config.ConnectRetryMax = 6;
        for (var i = 0; i < 10; i++) bt.ConnectErrors.Enqueue(Fail(BluetoothFailureKind.HidNotReady, "HID input never started"));
        string? notice = null;
        engine.OnNotice = (title, body) => { if (title == "Connection failed") notice = body; };

        await Arrive(engine);

        Assert.Equal(2, Count(bt, "connect"));     // stopped after 2, not 6
        Assert.Equal(0, Count(bt, "unpair"));      // no stale-bond re-pair
        Assert.Contains("off and on", engine.Statuses.Values.Single().Label);
        Assert.Contains("off and on", notice);
        Assert.True(log.Has(LogLevel.Error, "wedged"), log.Dump());
    }

    [Fact]
    public async Task Hid_not_ready_once_then_recovers()
    {
        var engine = MakeEngine(out var bt, out _, out _);
        bt.ConnectErrors.Enqueue(Fail(BluetoothFailureKind.HidNotReady, "HID input never started"));

        await Arrive(engine);

        Assert.Equal(DeviceStatus.Connected, engine.Statuses.Values.Single());
        Assert.Equal(0, Count(bt, "unpair"));
    }

    // ---- Spec §3 connect flow: keep the pairing link, wait for HID on it ----

    /// HID comes up on the pairing link: done — no drop, no settle, no reconnect.
    [Fact]
    public async Task Hid_on_pairing_link_skips_drop_and_reconnect()
    {
        var engine = MakeEngine(out var bt, out _, out var log);
        bt.HidReady = addr => bt.Paired.Contains(addr);

        await Arrive(engine);

        Assert.Equal(new[] { $"pair:{N}" }, bt.Calls);
        Assert.Equal(DeviceStatus.Connected, engine.Statuses.Values.Single());
        Assert.True(log.Has(LogLevel.Info, "HID on the pairing link"), log.Dump());
    }

    /// No HID on the pairing link within the wait: fall back to drop + settle + connect, and
    /// accept once HID is up after the connect.
    [Fact]
    public async Task No_hid_on_pairing_link_falls_back_to_drop_and_reconnect()
    {
        var engine = MakeEngine(out var bt, out _, out _);
        bt.HidReady = addr => bt.Calls.Contains($"connect:{addr}");

        await Arrive(engine);

        Assert.Equal(new[] { $"pair:{N}", $"disconnect:{N}", $"connect:{N}" }, bt.Calls);
        Assert.Equal(DeviceStatus.Connected, engine.Statuses.Values.Single());
    }

    /// Link up after connect but HID never: HidNotReady, detected by the engine (not thrown by the
    /// backend); twice ends the run with the power-cycle hint.
    [Fact]
    public async Task Engine_detects_hid_not_ready_after_connect()
    {
        var engine = MakeEngine(out var bt, out var config, out _);
        config.ConnectRetryMax = 6;
        bt.HidReady = _ => false;

        await Arrive(engine);

        Assert.Equal(2, Count(bt, "connect"));
        Assert.Equal(0, Count(bt, "unpair"));
        Assert.Contains("off and on", engine.Statuses.Values.Single().Label);
    }

    /// Unmanaged device: "already connected" only counts if HID isn't known to be down.
    [Fact]
    public async Task Unmanaged_already_connected_requires_hid()
    {
        var engine = MakeEngine(out var bt, out _, out _, managePairing: false);
        await bt.ConnectAsync(Addr);
        var hidUp = false;
        bt.HidReady = _ => hidUp;
        bt.ConnectError = null;
        bt.BeforeConnect = () => { hidUp = true; return Task.CompletedTask; };
        var callsBefore = bt.Calls.Count;

        await Arrive(engine);

        Assert.True(bt.Calls.Count > callsBefore, "link up but HID down must not count as already connected");
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
        // Pairing devices connect one at a time; devices with managePairing off run alongside them
        // (see Unmanaged_devices_connect_alongside_each_other).
        var engine = MakeEngine(out var bt, out var config, out var log, managePairing: true);
        config.Devices.Add(new BTDevice { Name = "Keyboard", Address = Addr2, Enabled = true, ManagePairing = true });

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
        await reached.Task;  // automation is mid-connect on the trackpad

        await engine.ConnectDeviceNowAsync(config.Devices[0]);
        release.SetResult();
        await arrival;

        Assert.True(log.Has(LogLevel.Debug, "supersedes"), log.Dump());
        Assert.DoesNotContain(Addr2.Replace(":", ""), bt.Connected);  // superseded run stopped
    }

    /// Polls a condition the engine's background tasks will make true (the fake's lists are read
    /// while the engine writes them, so a torn read just tries again).
    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try { if (condition()) return; } catch (InvalidOperationException) { }
            await Task.Delay(5);
        }
        throw new TimeoutException($"timed out waiting for: {what}");
    }

    [Fact]
    public async Task A_device_that_doesnt_answer_is_tried_slowly_and_connects_when_it_wakes()
    {
        // Spec §3: from two unanswered tries in a row, no retry budget and no give-up; a try
        // every SlowRetryMs until it connects or the KVM leaves, watched in between.
        var engine = MakeEngine(out var bt, out var config, out var log, managePairing: false);
        config.ConnectRetryMax = 2;
        bt.ConnectError = Fail(BluetoothFailureKind.NoAnswer, "didn't come");

        engine.HandleUsb(Vendor, Product, added: true);
        var arrival = engine.EvaluateNowAsync();
        await WaitFor(() => log.Has(LogLevel.Info, "doesn't answer"), "the slow-mode line");
        await WaitFor(() => Count(bt, "connect") >= 4, "tries past connectRetryMax");

        Assert.False(log.Has(LogLevel.Error, "gave up"), log.Dump());
        Assert.Equal(DeviceStatus.Disconnected, engine.Statuses.Values.Single());

        lock (bt.Connected) bt.Connected.Add(N);   // it wakes up and reconnects by itself
        await arrival.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DeviceStatus.Connected, engine.Statuses.Values.Single());
        Assert.True(log.Has(LogLevel.Info, "connected by itself"), log.Dump());
    }

    [Fact]
    public async Task Other_failures_in_slow_mode_stay_quiet()
    {
        // 2026-11-16: a switched-off speaker in slow mode failed a try with a 30 s timeout; it
        // flipped to "Problem" and back, and each flip popped the status panel.
        var engine = MakeEngine(out var bt, out var config, out var log, managePairing: false);
        bt.ConnectErrors.Enqueue(Fail(BluetoothFailureKind.NoAnswer, "didn't come"));
        bt.ConnectErrors.Enqueue(Fail(BluetoothFailureKind.NoAnswer, "didn't come"));
        bt.ConnectErrors.Enqueue(Fail(BluetoothFailureKind.Timeout, "no response within 30 s"));
        bt.ConnectError = Fail(BluetoothFailureKind.NoAnswer, "didn't come");
        var errorAfterSlow = false;
        engine.Changed += () =>
        {
            if (log.Has(LogLevel.Info, "doesn't answer")
                && engine.Statuses.Values.Any(s => s.Kind == DeviceStatusKind.Error)) errorAfterSlow = true;
        };

        engine.HandleUsb(Vendor, Product, added: true);
        var arrival = engine.EvaluateNowAsync();
        await WaitFor(() => Count(bt, "connect") >= 5, "slow tries past the timeout");

        Assert.False(errorAfterSlow, log.Dump());
        Assert.Equal(DeviceStatus.Disconnected, engine.Statuses.Values.Single());
        Assert.False(log.Has(LogLevel.Warn, "try 3"), log.Dump());
        await Leave(engine);
        await arrival.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Manual_connect_of_a_device_that_doesnt_answer_still_gives_up()
    {
        var engine = MakeEngine(out var bt, out var config, out var log, managePairing: false);
        config.ConnectRetryMax = 2;
        bt.ConnectError = Fail(BluetoothFailureKind.NoAnswer, "didn't come");

        await engine.ConnectDeviceNowAsync(config.Devices[0]).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("gave up", engine.Statuses.Values.Single().Label);
    }

    [Fact]
    public async Task A_device_that_didnt_answer_goes_last_next_time()
    {
        // The sleeping K380 (first in the list) waits until the speaker has connected.
        var engine = MakeEngine(out var bt, out var config, out var log, managePairing: false);
        config.Devices.Add(new BTDevice { Name = "Speaker", Address = Addr2, Enabled = true });
        var speaker = Addr2.Replace(":", "");
        bt.ConnectErrorFor = a => a == N ? Fail(BluetoothFailureKind.NoAnswer, "asleep") : null;

        engine.HandleUsb(Vendor, Product, added: true);
        var first = engine.EvaluateNowAsync();
        await WaitFor(() => log.Has(LogLevel.Info, "doesn't answer"), "the K380 in slow mode");
        await Leave(engine);
        await first;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var speakerWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bt.BeforeConnectFor = async a =>
        {
            if (a != speaker) return;
            speakerWaiting.TrySetResult();
            await gate.Task;
        };
        var before = Count(bt, "connect");
        engine.HandleUsb(Vendor, Product, added: true);
        var second = engine.EvaluateNowAsync();
        await speakerWaiting.Task;
        await Task.Delay(200);
        Assert.Equal(before, Count(bt, "connect"));   // the K380 hasn't been tried yet
        Assert.True(log.Has(LogLevel.Debug, "waiting for the devices that answer"), log.Dump());

        gate.SetResult();
        await WaitFor(() => Count(bt, "connect") > before, "the K380's try after the speaker");
        await Leave(engine);
        await second;
    }

    [Fact]
    public async Task Trackpad_unpair_does_not_wait_for_a_speaker_disconnect()
    {
        // 2026-11-15: the speaker's audio disconnect (3-4 s) ran ahead of the trackpad's unpair,
        // and the Mac's trackpad connect got 4-5 s slower. The speaker is first in the list.
        var engine = MakeEngine(out var bt, out var config, out var log, managePairing: true);
        var speaker = Addr2.Replace(":", "");
        config.Devices.Insert(0, new BTDevice { Name = "Speaker", Address = Addr2, Enabled = true });
        await Arrive(engine);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bt.BeforeDisconnectFor = async a => { if (a == speaker) await gate.Task; };
        engine.HandleUsb(Vendor, Product, added: false);
        var leave = engine.EvaluateNowAsync();
        await WaitFor(() => bt.Calls.Contains($"unpair:{N}"), "the trackpad unpair");
        Assert.DoesNotContain($"disconnect:{speaker}", bt.Calls);   // the speaker is still on its way out

        gate.SetResult();
        await leave;
        Assert.Contains($"disconnect:{speaker}", bt.Calls);
    }

    [Fact]
    public async Task Unmanaged_devices_connect_alongside_each_other()
    {
        // 2026-11-15: a K380 that never answered spent 59 s on retries while a speaker listed
        // after it waited. Devices that don't pair connect at the same time.
        var engine = MakeEngine(out var bt, out var config, out var log, managePairing: false);
        config.Devices.Add(new BTDevice { Name = "Speaker", Address = Addr2, Enabled = true });

        var first = Addr.Replace(":", "");
        var second = Addr2.Replace(":", "");
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        bt.BeforeConnect = async () =>
        {
            if (Interlocked.Increment(ref calls) == 1) await stuck.Task;   // the first device hangs
        };

        engine.HandleUsb(Vendor, Product, added: true);
        var arrival = engine.EvaluateNowAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!bt.Connected.Contains(second) && DateTime.UtcNow < deadline) await Task.Delay(10);

        Assert.Contains(second, bt.Connected);     // connected while the first was still stuck
        Assert.DoesNotContain(first, bt.Connected);
        stuck.SetResult();
        await arrival;
        Assert.Contains(first, bt.Connected);
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
