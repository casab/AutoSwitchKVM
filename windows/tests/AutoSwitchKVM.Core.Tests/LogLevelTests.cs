using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using AutoSwitchKVM.Core.Tests.Fakes;
using Xunit;

namespace AutoSwitchKVM.Core.Tests;

/// Log level policy (spec §10): the default level tells the story of a handoff without the step
/// detail; expected refusals are debug, real failures warnings, give-ups errors.
public class LogLevelTests
{
    private const ushort Vendor = 0x05E3;
    private const ushort Product = 0x0626;
    private const string Addr = "3C:50:02:BF:22:45";

    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("info", LogLevel.Info)]
    [InlineData("warning", LogLevel.Warn)]
    [InlineData("warn", LogLevel.Warn)]
    [InlineData("error", LogLevel.Error)]
    [InlineData(" DEBUG ", LogLevel.Debug)]
    [InlineData(null, LogLevel.Info)]
    [InlineData("bogus", LogLevel.Info)]
    public void Config_string_parses(string? value, LogLevel expected) =>
        Assert.Equal(expected, LogLevels.Parse(value));

    [Theory]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Info)]
    [InlineData(LogLevel.Warn)]
    [InlineData(LogLevel.Error)]
    public void Config_string_round_trips(LogLevel level) =>
        Assert.Equal(level, LogLevels.Parse(LogLevels.ToConfig(level)));

    private static SelectionEngine MakeEngine(out FakeBluetoothController bt, out ListLogSink log)
    {
        var config = AppConfig.Default();
        config.Source = new USBSource { Name = "Hub", VendorID = Vendor, ProductIDs = new() { Product } };
        config.Devices = new List<BTDevice> { new() { Name = "Trackpad", Address = Addr, Enabled = true, ManagePairing = true } };
        bt = new FakeBluetoothController();
        log = new ListLogSink();
        return new SelectionEngine(config, new FakeUsbMonitor(), bt, log) { SleepHook = _ => Task.CompletedTask };
    }

    private static List<string> AtOrAbove(ListLogSink log, LogLevel min)
    {
        lock (log.Entries) return log.Entries.Where(e => e.Level >= min).Select(e => e.Message).ToList();
    }

    [Fact]
    public async Task Normal_handoff_logs_a_short_story_at_info()
    {
        var engine = MakeEngine(out _, out var log);
        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();
        engine.HandleUsb(Vendor, Product, added: false);
        await engine.EvaluateNowAsync();

        var info = AtOrAbove(log, LogLevel.Info);
        Assert.Contains(info, m => m.Contains("source present"));
        Assert.Contains(info, m => m.Contains("connected after 1 try"));
        Assert.Contains(info, m => m.Contains("source gone"));
        Assert.Contains(info, m => m.Contains("released (unpaired)"));
        // Step detail is debug-only.
        Assert.DoesNotContain(info, m => m.Contains("pair OK"));
        Assert.DoesNotContain(info, m => m.Contains("connect started"));
        Assert.True(info.Count <= 6, string.Join("\n", info));
    }

    [Fact]
    public async Task Expected_refusal_is_debug_but_a_real_failure_is_a_warning()
    {
        var engine = MakeEngine(out var bt, out var log);
        bt.PairErrors.Enqueue(new BluetoothException(BluetoothFailureKind.DeviceBusy, "AuthenticationNotAllowed"));
        bt.PairErrors.Enqueue(new BluetoothException(BluetoothFailureKind.AuthenticationFailed, "AuthenticationFailure"));

        engine.HandleUsb(Vendor, Product, added: true);
        await engine.EvaluateNowAsync();

        Assert.True(log.Has(LogLevel.Debug, "AuthenticationNotAllowed"), log.Dump());
        Assert.False(log.Has(LogLevel.Warn, "AuthenticationNotAllowed"), log.Dump());
        Assert.True(log.Has(LogLevel.Warn, "AuthenticationFailure"), log.Dump());
    }
}
