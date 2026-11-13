using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using AutoSwitchKVM.Core.Tests.Fakes;
using Xunit;

namespace AutoSwitchKVM.Core.Tests;

/// Adding, enabling or removing a device touches only that device (2026-11-13: a full re-seed on
/// every list change put working devices through pair + HID check again).
public class DeviceListChangeTests
{
    private const ushort Vendor = 0x05E3;
    private const ushort Product = 0x0626;
    private const string Trackpad = "3C:50:02:BF:22:45";
    private const string Keyboard = "E7:99:A2:72:A6:76";
    private static string Norm(string a) => a.Replace(":", "").Replace("-", "").ToUpperInvariant();

    private static SelectionEngine MakeSelectedEngine(out FakeBluetoothController bt, out AppConfig config)
    {
        config = AppConfig.Default();
        config.Source = new USBSource { Name = "Hub", VendorID = Vendor, ProductIDs = new() { Product } };
        config.Devices = new List<BTDevice>
        {
            new() { Name = "Trackpad", Address = Trackpad, Enabled = true, ManagePairing = true },
        };
        bt = new FakeBluetoothController();
        return new SelectionEngine(config, new FakeUsbMonitor(), bt) { SleepHook = _ => Task.CompletedTask };
    }

    [Fact]
    public async Task Added_device_connects_alone_while_selected()
    {
        var engine = MakeSelectedEngine(out var bt, out var config);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        bt.Calls.Clear();

        var keyboard = new BTDevice { Name = "Keyboard", Address = Keyboard, Enabled = true, ManagePairing = false };
        config.Devices.Add(keyboard);
        await engine.ConnectAddedDeviceAsync(keyboard.Id);

        Assert.Contains(Norm(Keyboard), bt.Connected);
        Assert.DoesNotContain(bt.Calls, c => c.EndsWith(Norm(Trackpad)));   // the trackpad was left alone
    }

    [Fact]
    public async Task Added_device_waits_when_switched_away()
    {
        var engine = MakeSelectedEngine(out var bt, out var config);
        await engine.EvaluateNowAsync();   // source absent: not selected
        bt.Calls.Clear();

        var keyboard = new BTDevice { Name = "Keyboard", Address = Keyboard, Enabled = true };
        config.Devices.Add(keyboard);
        await engine.ConnectAddedDeviceAsync(keyboard.Id);

        Assert.Empty(bt.Calls);
    }

    [Fact]
    public async Task Added_device_waits_when_paused_or_disabled()
    {
        var engine = MakeSelectedEngine(out var bt, out var config);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        bt.Calls.Clear();

        var disabled = new BTDevice { Name = "Mouse", Address = "D4:A1:9B:44:55:66", Enabled = false };
        config.Devices.Add(disabled);
        await engine.ConnectAddedDeviceAsync(disabled.Id);

        config.Paused = true;
        var keyboard = new BTDevice { Name = "Keyboard", Address = Keyboard, Enabled = true };
        config.Devices.Add(keyboard);
        await engine.ConnectAddedDeviceAsync(keyboard.Id);

        Assert.Empty(bt.Calls);
    }

    [Fact]
    public async Task Removed_device_is_forgotten_without_touching_the_others()
    {
        var engine = MakeSelectedEngine(out var bt, out var config);
        var keyboard = new BTDevice { Name = "Keyboard", Address = Keyboard, Enabled = true };
        config.Devices.Add(keyboard);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        Assert.True(engine.Statuses.ContainsKey(keyboard.Id));
        bt.Calls.Clear();

        engine.ForgetDevice(keyboard.Id);
        config.Devices.Remove(keyboard);

        Assert.False(engine.Statuses.ContainsKey(keyboard.Id));
        Assert.Empty(bt.Calls);
    }
}
