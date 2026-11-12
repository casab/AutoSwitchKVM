using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using AutoSwitchKVM.Core.Tests.Fakes;
using Xunit;

namespace AutoSwitchKVM.Core.Tests;

/// Fixes from the 2026-11-12 code review: a pair overtaken by a release is undone, a pair waits for
/// a recent unpair, a seed with the source absent releases what we hold (spec §3 Evaluate step 5),
/// a failed seed snapshot keeps state, and a run that goes stale during the HID wait never reports
/// "Connected".
public class HandoffRaceTests
{
    private const ushort Vendor = 0x05E3;
    private const ushort Product = 0x0626;
    private const string Addr = "3C:50:02:BF:22:45";
    private static readonly string N = Addr.Replace(":", "");

    private static SelectionEngine MakeEngine(out FakeBluetoothController bt, out AppConfig config,
        out ListLogSink log, out FakeUsbMonitor usb, bool managePairing = true)
    {
        config = AppConfig.Default();
        config.Source = new USBSource { Name = "Hub", VendorID = Vendor, ProductIDs = new() { Product } };
        config.Devices = new List<BTDevice>
        {
            new() { Name = "Trackpad", Address = Addr, Enabled = true, ManagePairing = managePairing },
        };
        bt = new FakeBluetoothController();
        log = new ListLogSink();
        usb = new FakeUsbMonitor();
        return new SelectionEngine(config, usb, bt, log)
        {
            SleepHook = _ => Task.CompletedTask,
            CallTimeoutOverride = TimeSpan.FromSeconds(5),
        };
    }

    private static Task Arrive(SelectionEngine e) { e.HandleUsb(Vendor, Product, true); return e.EvaluateNowAsync(); }
    private static Task Leave(SelectionEngine e) { e.HandleUsb(Vendor, Product, false); return e.EvaluateNowAsync(); }
    private static int Count(FakeBluetoothController bt, string call) => bt.Calls.Count(c => c == $"{call}:{N}");

    private static (TaskCompletionSource Reached, TaskCompletionSource Release, Func<Task> Hook) OneShotGate()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = true;
        return (reached, release, async () =>
        {
            if (!armed) return;
            armed = false;
            reached.SetResult();
            await release.Task;
        });
    }

    // ---- A pair that completes after a release is undone (spec §3) ----

    [Fact]
    public async Task Pair_completed_after_a_release_is_undone()
    {
        var engine = MakeEngine(out var bt, out _, out var log, out _);
        var gate = OneShotGate();
        bt.BeforePair = gate.Hook;

        engine.HandleUsb(Vendor, Product, true);
        var arrival = engine.EvaluateNowAsync();
        await gate.Reached.Task;           // the pair is in flight
        await Leave(engine);               // the release runs meanwhile
        gate.Release.SetResult();          // the pair now completes, after the release
        await arrival;

        Assert.DoesNotContain(N, bt.Paired);                 // the late bond was undone
        Assert.Equal(2, Count(bt, "unpair"));                // release + undo
        Assert.DoesNotContain($"connect:{N}", bt.Calls);
        Assert.True(log.Has(LogLevel.Warn, "new bond is undone"), log.Dump());
    }

    /// A KVM bounce: the stale run's pair lands after the release, but a newer connect wants the
    /// device. The bond must stay.
    [Fact]
    public async Task Bounce_keeps_the_bond_for_the_newer_connect()
    {
        var engine = MakeEngine(out var bt, out _, out _, out _);
        var gate = OneShotGate();
        bt.BeforePair = gate.Hook;

        engine.HandleUsb(Vendor, Product, true);
        var arrival = engine.EvaluateNowAsync();
        await gate.Reached.Task;
        await Leave(engine);
        await Arrive(engine);              // the source is back: a newer connect run
        gate.Release.SetResult();
        await arrival;

        Assert.Contains(N, bt.Paired);
        Assert.Equal(1, Count(bt, "unpair"));                // only the release
        Assert.Equal(DeviceStatus.Connected, engine.Statuses.Values.Single());
    }

    /// Review finding 1: a release arriving during the stale-bond unpair used to be overtaken by
    /// the pair that followed it, leaving the trackpad bonded with the source gone.
    [Fact]
    public async Task Release_during_stale_bond_unpair_stops_the_pair()
    {
        var engine = MakeEngine(out var bt, out var config, out _, out _);
        config.ConnectRetryMax = 3;
        bt.ConnectErrors.Enqueue(new BluetoothException("dead link key"));   // try 1 fails → fresh bond
        var gate = OneShotGate();
        bt.BeforeUnpair = gate.Hook;       // try 2's stale-bond unpair suspends here

        engine.HandleUsb(Vendor, Product, true);
        var arrival = engine.EvaluateNowAsync();
        await gate.Reached.Task;
        await Leave(engine);
        gate.Release.SetResult();
        await arrival;

        Assert.Equal(1, Count(bt, "pair"));                  // try 2 never paired
        Assert.DoesNotContain(N, bt.Paired);
    }

    // ---- A pair waits for a recent unpair (spec §3) ----

    [Fact]
    public async Task Pair_waits_for_an_unpair_still_in_flight()
    {
        var engine = MakeEngine(out var bt, out _, out _, out _);
        await Arrive(engine);
        var gate = OneShotGate();
        bt.BeforeUnpair = gate.Hook;

        engine.HandleUsb(Vendor, Product, false);
        var departure = engine.EvaluateNowAsync();
        await gate.Reached.Task;           // the release's unpair is in flight
        var arrival = Arrive(engine);      // KVM bounce
        await Task.Delay(50);
        Assert.Equal(1, Count(bt, "pair"));                  // still waiting: no second pair yet
        gate.Release.SetResult();
        await departure;
        await arrival;

        var lastUnpair = bt.Calls.LastIndexOf($"unpair:{N}");
        var lastPair = bt.Calls.LastIndexOf($"pair:{N}");
        Assert.True(lastUnpair < lastPair, string.Join(", ", bt.Calls));
        Assert.Contains(N, bt.Paired);
    }

    [Fact]
    public async Task Pair_settles_after_a_recent_unpair()
    {
        var engine = MakeEngine(out var bt, out _, out _, out _);
        var now = new DateTime(2026, 11, 12, 23, 0, 0, DateTimeKind.Utc);
        engine.Clock = () => now;
        var sleeps = new List<int>();
        engine.SleepHook = ms => { sleeps.Add(ms); return Task.CompletedTask; };

        await Arrive(engine);
        await Leave(engine);               // unpair returns at "now"
        sleeps.Clear();
        now = now.AddMilliseconds(500);
        await Arrive(engine);

        Assert.Contains(1500, sleeps);     // 2000 ms gap − 500 ms already passed
    }

    // ---- Spec §3 Evaluate step 5: reconcile on seed ----

    [Fact]
    public async Task Seed_with_source_absent_releases_a_held_device()
    {
        var engine = MakeEngine(out var bt, out _, out _, out _);
        bt.Paired.Add(N);
        bt.Connected.Add(N);
        var notices = new List<string>();
        engine.OnNotice = (t, _) => notices.Add(t);

        await engine.SeedAsync();          // the app starts while switched away
        await engine.EvaluateNowAsync();

        Assert.Equal(1, Count(bt, "unpair"));
        Assert.DoesNotContain(N, bt.Paired);
        Assert.Contains("Disconnected", notices);            // it was connected, so say so
    }

    [Fact]
    public async Task Seed_release_is_quiet_when_nothing_was_connected()
    {
        var engine = MakeEngine(out var bt, out _, out _, out _);
        var notices = new List<string>();
        engine.OnNotice = (t, _) => notices.Add(t);

        await engine.SeedAsync();
        await engine.EvaluateNowAsync();

        Assert.Equal(1, Count(bt, "unpair"));                // idempotent unpair, just in case
        Assert.Empty(notices);
    }

    [Fact]
    public async Task Seed_reconcile_runs_once_not_on_every_evaluation()
    {
        var engine = MakeEngine(out var bt, out _, out _, out _);
        await engine.SeedAsync();
        await engine.EvaluateNowAsync();
        await engine.EvaluateNowAsync();   // e.g. an unrelated USB change

        Assert.Equal(1, Count(bt, "unpair"));
    }

    /// The review's main scenario: pause → switch away → unpause must release.
    [Fact]
    public async Task Pause_switch_away_unpause_releases_the_device()
    {
        var engine = MakeEngine(out var bt, out var config, out _, out _);
        await Arrive(engine);
        Assert.Contains(N, bt.Paired);

        config.Paused = true;
        await Leave(engine);               // paused: no release
        Assert.Contains(N, bt.Paired);

        config.Paused = false;
        await engine.ReevaluateAsync();    // what TogglePause does
        await engine.EvaluateNowAsync();

        Assert.DoesNotContain(N, bt.Paired);
    }

    [Fact]
    public async Task Seed_reconcile_waits_while_paused()
    {
        var engine = MakeEngine(out var bt, out var config, out _, out _);
        config.Paused = true;
        await engine.SeedAsync();
        await engine.EvaluateNowAsync();
        Assert.Equal(0, Count(bt, "unpair"));
    }

    // ---- Seed snapshot failure ----

    [Fact]
    public async Task Failed_seed_snapshot_keeps_presence_and_still_evaluates()
    {
        var engine = MakeEngine(out var bt, out _, out var log, out var usb);
        await Arrive(engine);
        usb.SnapshotError = new InvalidOperationException("WMI not ready");

        await engine.SeedAsync();          // e.g. resume
        await engine.EvaluateNowAsync();

        Assert.True(engine.Selected);                        // presence kept, not wiped
        Assert.True(log.Has(LogLevel.Warn, "USB snapshot failed"), log.Dump());
        Assert.Equal(0, Count(bt, "unpair"));                // no false release
    }

    // ---- Staleness during the HID wait ----

    [Fact]
    public async Task Run_going_stale_during_the_hid_wait_never_reports_connected()
    {
        var engine = MakeEngine(out var bt, out _, out _, out _);
        var fired = false;
        bt.HidReady = _ =>
        {
            if (!fired) { fired = true; engine.NotifySuspending(); }   // sleep starts mid-wait
            return false;
        };
        var notices = new List<string>();
        engine.OnNotice = (t, _) => notices.Add(t);

        await Arrive(engine);

        Assert.DoesNotContain("Connected", notices);
        Assert.NotEqual(DeviceStatus.Connected, engine.Statuses.Values.Single());
        Assert.DoesNotContain(N, bt.Paired);                 // suspend release → late bond undone
    }
}
