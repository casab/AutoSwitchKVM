using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using AutoSwitchKVM.Core.Tests.Fakes;
using Xunit;

namespace AutoSwitchKVM.Core.Tests;

/// Ports osx/Tests/AutoSwitchKVMTests/SelectionEngineTests.swift. Drives the engine via the
/// no-timer seams (HandleUsb + EvaluateNowAsync) with an instant SleepHook for determinism.
public class SelectionEngineTests
{
    private const ushort Vendor = 0x05E3;
    private const ushort Product = 0x0626;
    private const string Addr = "3C:50:02:BF:22:45";
    private static string Norm(string a) => a.Replace(":", "").Replace("-", "").ToUpperInvariant();

    private static SelectionEngine MakeEngine(out FakeBluetoothController bt, out AppConfig config,
                                              bool managePairing = false, bool enabled = true)
    {
        config = AppConfig.Default();
        config.Source = new USBSource { Name = "Hub", VendorID = Vendor, ProductIDs = new() { Product } };
        config.Devices = new List<BTDevice>
        {
            new() { Name = "Trackpad", Address = Addr, Enabled = enabled, ManagePairing = managePairing }
        };
        var usb = new FakeUsbMonitor();
        bt = new FakeBluetoothController();
        return new SelectionEngine(config, usb, bt) { SleepHook = _ => Task.CompletedTask };
    }

    [Fact]
    public async Task Connects_when_source_arrives()
    {
        var engine = MakeEngine(out var bt, out _);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();

        Assert.True(engine.Selected);
        Assert.Contains(Norm(Addr), bt.Connected);
    }

    [Fact]
    public async Task Disconnects_when_source_leaves()
    {
        var engine = MakeEngine(out var bt, out _);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        Assert.True(engine.Selected);

        engine.HandleUsb(Vendor, Product, added: false);
        await engine.EvaluateNowAsync();

        Assert.False(engine.Selected);
        Assert.DoesNotContain(Norm(Addr), bt.Connected);
    }

    [Fact]
    public async Task Pairs_before_connect_when_manage_pairing()
    {
        var engine = MakeEngine(out var bt, out _, managePairing: true);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();

        var pairIndex = bt.Calls.IndexOf($"pair:{Norm(Addr)}");
        var connectIndex = bt.Calls.IndexOf($"connect:{Norm(Addr)}");
        Assert.True(pairIndex >= 0 && connectIndex >= 0, $"expected pair and connect; got [{string.Join(", ", bt.Calls)}]");
        Assert.True(pairIndex < connectIndex, "pair must happen before connect");
    }

    [Fact]
    public async Task Unpairs_on_leave_when_manage_pairing()
    {
        var engine = MakeEngine(out var bt, out _, managePairing: true);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();

        engine.HandleUsb(Vendor, Product, added: false);
        await engine.EvaluateNowAsync();

        Assert.Contains($"unpair:{Norm(Addr)}", bt.Calls);
    }

    [Fact]
    public async Task Does_not_unpair_on_leave_when_not_managing_pairing()
    {
        var engine = MakeEngine(out var bt, out _, managePairing: false);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        engine.HandleUsb(Vendor, Product, added: false);
        await engine.EvaluateNowAsync();

        Assert.DoesNotContain($"unpair:{Norm(Addr)}", bt.Calls);
    }

    [Fact]
    public async Task Ignores_unrelated_usb_device()
    {
        var engine = MakeEngine(out var bt, out _);
        engine.HandleUsb(0x1234, 0x5678, added: true);
        await engine.EvaluateNowAsync();

        Assert.False(engine.Selected);
        Assert.Empty(bt.Connected);
    }

    [Fact]
    public async Task Disabled_device_is_not_connected()
    {
        var engine = MakeEngine(out var bt, out _, enabled: false);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();

        Assert.True(engine.Selected);     // source is present
        Assert.Empty(bt.Connected);       // but disabled device is left alone
    }

    [Fact]
    public async Task Multi_device_source_stays_selected_until_all_gone()
    {
        const ushort p1 = 0x0626;
        const ushort p2 = 0x0610;
        var config = AppConfig.Default();
        config.Source = new USBSource { Name = "KVM", VendorID = Vendor, ProductIDs = new() { p1, p2 } };
        config.Devices = new List<BTDevice> { new() { Name = "Trackpad", Address = Addr, Enabled = true } };
        var engine = new SelectionEngine(config, new FakeUsbMonitor(), new FakeBluetoothController())
        {
            SleepHook = _ => Task.CompletedTask
        };

        engine.HandleUsb(Vendor, p1, added: true);
        engine.HandleUsb(Vendor, p2, added: true);
        await engine.EvaluateNowAsync();
        Assert.True(engine.Selected);

        engine.HandleUsb(Vendor, p1, added: false);  // one leaves
        await engine.EvaluateNowAsync();
        Assert.True(engine.Selected);

        engine.HandleUsb(Vendor, p2, added: false);  // last leaves
        await engine.EvaluateNowAsync();
        Assert.False(engine.Selected);
    }

    [Fact]
    public async Task Paused_skips_automatic_connect()
    {
        var engine = MakeEngine(out var bt, out var config);
        config.Paused = true;

        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();

        Assert.True(engine.Selected, "selection should reflect real presence even while paused");
        Assert.Empty(bt.Connected);
    }

    [Fact]
    public async Task Resuming_from_pause_reconciles_state()
    {
        var engine = MakeEngine(out var bt, out var config);
        config.Paused = true;

        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        Assert.Empty(bt.Connected);

        config.Paused = false;
        await engine.EvaluateNowAsync();
        Assert.Contains(Norm(Addr), bt.Connected);
    }

    [Fact]
    public async Task ConnectAllNow_works_regardless_of_selection()
    {
        var engine = MakeEngine(out var bt, out _);
        // Source not present, so automation wouldn't connect.
        await engine.ConnectAllNowAsync();
        Assert.Contains(Norm(Addr), bt.Connected);
    }

    [Fact]
    public async Task DisconnectAllNow_disconnects()
    {
        var engine = MakeEngine(out var bt, out _);
        await bt.ConnectAsync(Addr);
        await engine.DisconnectAllNowAsync();
        Assert.DoesNotContain(Norm(Addr), bt.Connected);
    }

    [Fact]
    public async Task Bluetooth_off_skips_connect()
    {
        var engine = MakeEngine(out var bt, out _);
        bt.PoweredOn = false;
        await engine.RefreshPowerAsync();
        Assert.False(engine.BluetoothPowered);

        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();

        Assert.True(engine.Selected);                 // presence still tracked
        Assert.Empty(bt.Connected);                   // but no connect while BT is off
        Assert.Equal(DeviceStatus.BluetoothOff, engine.Statuses.Values.Single());
    }

    [Fact]
    public void Backoff_is_exponential_and_capped()
    {
        Assert.Equal(2, SelectionEngine.BackoffSeconds(2, 1));
        Assert.Equal(4, SelectionEngine.BackoffSeconds(2, 2));
        Assert.Equal(8, SelectionEngine.BackoffSeconds(2, 3));
        Assert.Equal(16, SelectionEngine.BackoffSeconds(2, 4));
        Assert.Equal(30, SelectionEngine.BackoffSeconds(2, 6));   // capped
    }

    /// The spec caps the retry backoff at 30 s — jitter must not push the real sleep past the cap.
    [Fact]
    public void Jittered_backoff_never_exceeds_cap()
    {
        var engine = MakeEngine(out _, out _);
        for (var attempt = 1; attempt <= 8; attempt++)
            for (var i = 0; i < 50; i++)
                Assert.InRange(engine.BackoffMillisJittered(2, attempt), 0, 30_000);
    }

    [Fact]
    public async Task Connects_in_device_list_order()
    {
        var config = AppConfig.Default();
        config.Source = new USBSource { Name = "Hub", VendorID = Vendor, ProductIDs = new() { Product } };
        config.Devices = new List<BTDevice>
        {
            new() { Name = "First", Address = "AA-AA", Enabled = true },
            new() { Name = "Second", Address = "BB-BB", Enabled = true },
        };
        var bt = new FakeBluetoothController();
        var engine = new SelectionEngine(config, new FakeUsbMonitor(), bt) { SleepHook = _ => Task.CompletedTask };

        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();

        Assert.Equal(new[] { "connect:AAAA", "connect:BBBB" }, bt.Calls);
    }

    [Fact]
    public async Task Already_connected_skips_redundant_connect()
    {
        var engine = MakeEngine(out var bt, out _);
        await bt.ConnectAsync(Addr);          // pretend it's already connected
        var callsBefore = bt.Calls.Count;

        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();

        Assert.Equal(callsBefore, bt.Calls.Count); // no redundant connect
    }

    // ---- Spec conformance (mirrored in osx SelectionEngineTests) ----

    /// Spec §3: on giving up after connectRetryMax attempts, the device status is error.
    [Fact]
    public async Task Retry_exhaustion_sets_error_status()
    {
        var engine = MakeEngine(out var bt, out _);
        bt.ConnectError = new BluetoothException("nope");

        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();

        var status = engine.Statuses.Values.Single();
        Assert.Equal(DeviceStatusKind.Error, status.Kind);
        Assert.Contains("gave up", status.Label);
    }

    /// Spec §3: a passive drop while the source is present, BT is on, and no transition is in
    /// flight is an unexpected disconnect.
    [Fact]
    public async Task Unexpected_disconnect_fires_when_device_drops_while_selected()
    {
        var engine = MakeEngine(out var bt, out _);
        BTDevice? dropped = null;
        engine.OnUnexpectedDisconnect = d => dropped = d;
        engine.RefreshMonitoring();

        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        Assert.Equal(DeviceStatus.Connected, engine.Statuses.Values.Single());

        bt.FireConnectionChange(Addr, connected: false);  // passive drop, source still present

        Assert.Equal("Trackpad", dropped?.Name);
        Assert.Equal(DeviceStatus.Disconnected, engine.Statuses.Values.Single());
    }

    /// Spec §3 connect flow: pair → drop the transient post-pair link → clean reconnect,
    /// in exactly that order.
    [Fact]
    public async Task Managed_connect_drops_transient_link_then_reconnects()
    {
        var engine = MakeEngine(out var bt, out _, managePairing: true);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();

        Assert.Equal(
            new[] { $"pair:{Norm(Addr)}", $"disconnect:{Norm(Addr)}", $"connect:{Norm(Addr)}" },
            bt.Calls);
    }

    /// Spec §3: the per-device connect delay re-checks cancellation after the wait.
    [Fact]
    public async Task Connect_delay_aborts_when_superseded_during_wait()
    {
        var config = AppConfig.Default();
        config.Source = new USBSource { Name = "Hub", VendorID = Vendor, ProductIDs = new() { Product } };
        config.Devices = new List<BTDevice>
        {
            new() { Name = "Trackpad", Address = Addr, Enabled = true, ConnectDelayMs = 100 },
        };
        var bt = new FakeBluetoothController();
        var engine = new SelectionEngine(config, new FakeUsbMonitor(), bt);

        // Gate the engine's sleep (the connect delay) so the supersede is deterministic.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SleepHook = _ => { reached.TrySetResult(); return release.Task; };

        engine.HandleUsb(Vendor, Product, added: true);
        var arrival = engine.EvaluateNowAsync();
        await reached.Task;

        // Supersede during the delay: the source leaves and the departure run starts.
        engine.HandleUsb(Vendor, Product, added: false);
        await engine.EvaluateNowAsync();

        release.SetResult();
        await arrival;

        Assert.DoesNotContain($"connect:{Norm(Addr)}", bt.Calls);
    }

    /// Spec §3: on Bluetooth off → on, re-seed and reconnect if the source is present.
    [Fact]
    public async Task Power_on_reseeds_and_reconnects()
    {
        var config = AppConfig.Default();
        config.Source = new USBSource { Name = "Hub", VendorID = Vendor, ProductIDs = new() { Product } };
        config.Devices = new List<BTDevice> { new() { Name = "Trackpad", Address = Addr, Enabled = true } };
        var usb = new FakeUsbMonitor();
        var bt = new FakeBluetoothController();
        var engine = new SelectionEngine(config, usb, bt) { SleepHook = _ => Task.CompletedTask };

        bt.PoweredOn = false;
        await engine.RefreshPowerAsync();
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        Assert.Empty(bt.Connected);

        usb.SetDevices(new UsbDeviceInfo(Vendor, Product, "Hub", "USB\\VID_05E3&PID_0626"));
        bt.PoweredOn = true;
        await engine.RefreshPowerAsync();   // → SeedAsync(): presence re-read from the snapshot
        await engine.EvaluateNowAsync();    // drive the (normally debounced) evaluation deterministically

        Assert.Contains(Norm(Addr), bt.Connected);
    }

    // ---- Stale-run cancellation (mirrors the macOS tests) ----

    /// A KVM flipped away and back while the departure's disconnect is still in flight must not
    /// let the stale disconnect run continue: it would unpair/disconnect the devices the new
    /// arrival run just brought up, leaving them unusable with nothing scheduled to retry.
    [Fact]
    public async Task Stale_disconnect_run_aborts_when_source_returns()
    {
        const string Addr2 = "AA:BB:CC:DD:EE:FF";
        var config = AppConfig.Default();
        config.Source = new USBSource { Name = "Hub", VendorID = Vendor, ProductIDs = new() { Product } };
        config.Devices = new List<BTDevice>
        {
            new() { Name = "Trackpad", Address = Addr, Enabled = true, ManagePairing = true },
            new() { Name = "Keyboard", Address = Addr2, Enabled = true, ManagePairing = true },
        };
        var bt = new FakeBluetoothController();
        var engine = new SelectionEngine(config, new FakeUsbMonitor(), bt) { SleepHook = _ => Task.CompletedTask };

        // Source arrives; both devices come up paired + connected.
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        Assert.Contains(Norm(Addr), bt.Connected);
        Assert.Contains(Norm(Addr2), bt.Connected);

        // Arm a one-shot gate: the next disconnect (the departure run's first) suspends until released.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = true;
        bt.BeforeDisconnect = async () =>
        {
            if (!armed) return;
            armed = false;
            reached.SetResult();
            await release.Task;
        };

        // Source leaves; the departure run suspends inside device 1's disconnect.
        engine.HandleUsb(Vendor, Product, added: false);
        var departure = engine.EvaluateNowAsync();
        await reached.Task;

        // Source returns; the arrival run reconnects everything while the stale run is suspended.
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        Assert.Contains(Norm(Addr), bt.Connected);
        Assert.Contains(Norm(Addr2), bt.Connected);

        // Release the stale run: it must notice it is stale and stop — no unpair of device 1,
        // and device 2 untouched.
        release.SetResult();
        await departure;

        Assert.Contains(Norm(Addr), bt.Paired);      // stale run must not unpair device 1
        Assert.Contains(Norm(Addr2), bt.Paired);     // stale run must not unpair device 2
        Assert.Contains(Norm(Addr2), bt.Connected);  // stale run must not disconnect device 2
        Assert.DoesNotContain($"unpair:{Norm(Addr2)}", bt.Calls);
    }

    /// Manual "Disconnect all now" must invalidate an in-flight automation connect run so the
    /// automation does not keep connecting devices the user just asked to release.
    [Fact]
    public async Task DisconnectAllNow_aborts_in_flight_connect_run()
    {
        const string Addr2 = "AA:BB:CC:DD:EE:FF";
        var config = AppConfig.Default();
        config.Source = new USBSource { Name = "Hub", VendorID = Vendor, ProductIDs = new() { Product } };
        config.Devices = new List<BTDevice>
        {
            new() { Name = "First", Address = Addr, Enabled = true },
            new() { Name = "Second", Address = Addr2, Enabled = true },
        };
        var bt = new FakeBluetoothController();
        var engine = new SelectionEngine(config, new FakeUsbMonitor(), bt) { SleepHook = _ => Task.CompletedTask };

        // One-shot gate: the arrival run suspends inside device 1's connect.
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
        await reached.Task;

        // User hits "Disconnect all now" while the connect run is mid-flight.
        await engine.DisconnectAllNowAsync();

        release.SetResult();
        await arrival;

        Assert.DoesNotContain(Norm(Addr2), bt.Connected); // superseded run must not continue to device 2
    }

    /// The bond is what blocks the other host (Magic Trackpad): even if the disconnect call
    /// fails, a managePairing device must still be unpaired on departure.
    [Fact]
    public async Task Unpair_still_attempted_when_disconnect_throws()
    {
        var engine = MakeEngine(out var bt, out _, managePairing: true);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        Assert.Contains(Norm(Addr), bt.Paired);

        bt.DisconnectError = new BluetoothException("disconnect failed");
        engine.HandleUsb(Vendor, Product, added: false);
        await engine.EvaluateNowAsync();

        Assert.Contains($"unpair:{Norm(Addr)}", bt.Calls);      // unpair must still run
        Assert.DoesNotContain(Norm(Addr), bt.Paired);           // bond released for the other host
        Assert.Equal(DeviceStatus.Disconnected, engine.Statuses.Values.Single());
    }
}
