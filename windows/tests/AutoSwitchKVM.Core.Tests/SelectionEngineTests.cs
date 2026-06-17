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
}
