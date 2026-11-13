using AutoSwitchKVM.App.Support;
using AutoSwitchKVM.Core;
using HidSharp;

namespace AutoSwitchKVM.App.Platform;

/// IHostSwitcher for ZMK keyboards running the AutoSwitch firmware module
/// (github.com/casab/zmk-autoswitch; protocol in Core/ZmkHostProtocol.cs; SPECIFICATION.md §5a type 5).
/// The keyboard's "channels" are its Bluetooth profiles (BT_SEL 0..n).
///
/// Endpoint: the raw HID collection added by zzeneg/zmk-raw-hid (usage page 0xFF60, usage 0x61),
/// over Bluetooth LE (HID over GATT) or USB. Reports are 32 bytes without a report ID, so Windows
/// adds a leading 0 byte on both write and read. Any vendor ID: ZMK boards use many. The collection
/// is matched to the Bluetooth address through the device tree, like the Logitech endpoint.
public sealed class ZmkHostSwitcher : IHostSwitcher
{
    private const uint RawHidUsage = ((uint)ZmkHostProtocol.UsagePage << 16) | ZmkHostProtocol.Usage;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<HostChannels?> GetChannelsAsync(string address, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return await Task.Run(() => WithEndpoint(address, ReadChannels), ct); }
        finally { _lock.Release(); }
    }

    public async Task SwitchToAsync(string address, int channel, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var status = await Task.Run(() => WithEndpoint(address, s =>
                Request(s, ZmkHostProtocol.Select, (byte)channel) is { } reply ? (object?)ZmkHostProtocol.StatusOf(reply) : null), ct);
            if (status is null)
                throw new BluetoothException(BluetoothFailureKind.DeviceNotFound,
                    "the keyboard's AutoSwitch channel isn't available (not connected to this PC?)");
            if ((byte)status != ZmkHostProtocol.StatusOk)
                throw new BluetoothException(BluetoothFailureKind.Unknown,
                    $"the keyboard refused profile {channel + 1} (status {status})");
            Log.Debug("zmk", $"{address}: select profile {channel} acknowledged");
        }
        finally { _lock.Release(); }
    }

    public async Task MarkCurrentChannelAsync(string address, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await Task.Run(() => WithEndpoint(address, s =>
            {
                var ch = ReadChannels(s);
                if (ch is null || ch.Marks[ch.Current] == HostChannels.AutoSwitchMark) return null;
                var reply = Request(s, ZmkHostProtocol.SetMark, (byte)ch.Current, HostChannels.AutoSwitchMark);
                if (reply is null || ZmkHostProtocol.StatusOf(reply) != ZmkHostProtocol.StatusOk) return null;
                Log.Info("zmk", $"{address}: marked profile {ch.Current + 1} as this PC's (for the other computer's AutoSwitch)");
                return (object?)true;
            }), ct);
        }
        finally { _lock.Release(); }
    }

    // ---- plumbing ----

    private static HostChannels? ReadChannels(HidStream s) =>
        Request(s, ZmkHostProtocol.GetInfo) is { } reply ? ZmkHostProtocol.ParseInfo(reply) : null;

    /// Find the keyboard's raw HID endpoint and run `work`; null when there is none (not
    /// connected, or firmware without the module) or the device went away mid-call.
    private static T? WithEndpoint<T>(string address, Func<HidStream, T?> work) where T : class
    {
        var digits = address.Replace(":", "").Replace("-", "").ToUpperInvariant();
        var endpoint = FindEndpoint(digits);
        if (endpoint is null) return null;
        if (!endpoint.TryOpen(out HidStream stream)) return null;
        try
        {
            using (stream)
            {
                stream.ReadTimeout = (int)ReplyTimeout.TotalMilliseconds;
                return work(stream);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException or TimeoutException)
        {
            Log.Debug("zmk", $"{address}: HID connection lost ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// Send a request, wait for its reply (32 bytes, report ID stripped). null on timeout.
    private static byte[]? Request(HidStream s, byte command, params byte[] args)
    {
        var report = new byte[ZmkHostProtocol.ReportSize + 1];   // [0] = report ID 0
        ZmkHostProtocol.Request(command, args).CopyTo(report, 1);
        s.Write(report);
        var buf = new byte[ZmkHostProtocol.ReportSize + 1];
        var until = DateTime.UtcNow + ReplyTimeout;
        while (DateTime.UtcNow < until)
        {
            int n;
            try { n = s.Read(buf); }
            catch (TimeoutException) { return null; }
            if (n < 2) continue;
            var reply = buf.AsSpan(1, Math.Min(n - 1, ZmkHostProtocol.ReportSize)).ToArray();
            if (ZmkHostProtocol.IsReplyTo(reply, command)) return reply;
        }
        return null;
    }

    private static HidDevice? FindEndpoint(string digits)
    {
        foreach (var d in DeviceList.Local.GetHidDevices())
        {
            try
            {
                if (d.GetMaxOutputReportLength() < ZmkHostProtocol.ReportSize + 1) continue;
                if (!d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Contains(RawHidUsage))) continue;
                if (LogitechHostSwitcher.BelongsTo(d.DevicePath, digits)) return d;
            }
            catch (Exception ex)
            {
                Log.Debug("zmk", $"skipping HID interface ({ex.Message})");
            }
        }
        return null;
    }
}
