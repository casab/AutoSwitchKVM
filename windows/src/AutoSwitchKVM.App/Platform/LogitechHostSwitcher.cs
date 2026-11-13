using System.Runtime.InteropServices;
using AutoSwitchKVM.App.Support;
using AutoSwitchKVM.Core;
using HidSharp;

namespace AutoSwitchKVM.App.Platform;

/// IHostSwitcher over Logitech HID++ 2.0, feature 0x1814 "Change Host" (SPECIFICATION.md §5a
/// type 5). Works with Easy-Switch devices connected directly over Bluetooth (device index 0xFF),
/// e.g. the K380: verified on hardware 2026-11-13 (feature index 7, version 1, 3 channels).
/// Reference: reference_flow/ (offline Logitech Flow simulator) and its x1814 spec PDF.
///
///   [0] getHostInfo() -> nbHost, currHost, flags
///   [1] setCurrentHost(host)          (no reply: the device leaves this host)
///   [2] getCookies() -> one byte per host
///   [3] setCookie(host, cookie)       (AutoSwitch marks its own channel with HostChannels.AutoSwitchMark)
///
/// The HID++ endpoint is the device's vendor collection (usage page 0xFF00, 20-byte long report
/// 0x11). It is matched to the Bluetooth address through the device tree: an LE collection
/// carries the address in its path, a Classic one's parent (BTHENUM\{1124}…) does.
public sealed class LogitechHostSwitcher : IHostSwitcher
{
    private const int LogitechVendor = 0x046D;
    private const byte LongReport = 0x11;
    private const byte DirectDevice = 0xFF;      // a device connected straight to this host
    private const byte SoftwareId = 0x0B;        // ours; Logitech software uses other IDs
    private const ushort ChangeHostFeature = 0x1814;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, byte> _featureIndex = new();   // per address: 0 = not supported

    public async Task<HostChannels?> GetChannelsAsync(string address, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return await Task.Run(() => WithEndpoint(address, (s, fi) => ReadChannels(s, fi)), ct); }
        finally { _lock.Release(); }
    }

    public async Task SwitchToAsync(string address, int channel, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var done = await Task.Run(() => WithEndpoint(address, (s, fi) =>
            {
                Send(s, fi, 1, new[] { (byte)channel });   // setCurrentHost: no reply expected
                Log.Debug("hidpp", $"{address}: setCurrentHost({channel}) sent");
                return (object?)true;
            }), ct);
            if (done is null)
                throw new BluetoothException(BluetoothFailureKind.DeviceNotFound,
                    "the device's Logitech control channel isn't available (not connected to this PC?)");
        }
        finally { _lock.Release(); }
    }

    public async Task MarkCurrentChannelAsync(string address, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await Task.Run(() => WithEndpoint(address, (s, fi) =>
            {
                var ch = ReadChannels(s, fi);
                if (ch is null || ch.Current >= ch.Marks.Count || ch.Marks[ch.Current] == HostChannels.AutoSwitchMark) return (object?)null;
                Request(s, fi, 3, new[] { (byte)ch.Current, HostChannels.AutoSwitchMark });
                Log.Info("hidpp", $"{address}: marked channel {ch.Current + 1} as this PC's (for the other computer's AutoSwitch)");
                return (object?)true;
            }), ct);
        }
        finally { _lock.Release(); }
    }

    // ---- HID++ plumbing ----

    /// Find the device's HID++ endpoint, look up the Change Host feature (cached), run `work`.
    /// null when there is no endpoint (not connected) or no Change Host feature.
    private T? WithEndpoint<T>(string address, Func<HidStream, byte, T?> work) where T : class
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
                if (!_featureIndex.TryGetValue(digits, out var fi))
                {
                    var reply = Request(stream, 0x00, 0, new[] { (byte)(ChangeHostFeature >> 8), (byte)(ChangeHostFeature & 0xFF) });
                    fi = reply is null ? (byte)0 : reply[4];
                    if (reply is not null) _featureIndex[digits] = fi;
                    Log.Debug("hidpp", $"{address}: Change Host feature {(fi == 0 ? "not present" : $"at index {fi}")}");
                }
                return fi == 0 ? null : work(stream, fi);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException or TimeoutException)
        {
            // The device left mid-call (e.g. it just switched channel, or went to sleep).
            Log.Debug("hidpp", $"{address}: HID connection lost ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    private static HostChannels? ReadChannels(HidStream s, byte fi)
    {
        var info = Request(s, fi, 0, Array.Empty<byte>());
        if (info is null) return null;
        int count = info[4], current = info[5];
        var cookies = Request(s, fi, 2, Array.Empty<byte>());
        var marks = cookies is null ? new byte[count] : cookies.Skip(4).Take(count).ToArray();
        return new HostChannels(count, current, marks);
    }

    private static void Send(HidStream s, byte featureIndex, byte function, byte[] args)
    {
        var req = new byte[20];
        req[0] = LongReport;
        req[1] = DirectDevice;
        req[2] = featureIndex;
        req[3] = (byte)((function << 4) | SoftwareId);
        Array.Copy(args, 0, req, 4, Math.Min(args.Length, 16));
        s.Write(req);
    }

    /// Send a request and wait for its reply. null on timeout or a HID++ error reply.
    private static byte[]? Request(HidStream s, byte featureIndex, byte function, byte[] args)
    {
        Send(s, featureIndex, function, args);
        var buf = new byte[64];
        var until = DateTime.UtcNow + ReplyTimeout;
        while (DateTime.UtcNow < until)
        {
            int n;
            try { n = s.Read(buf); }
            catch (TimeoutException) { return null; }
            if (n < 7 || buf[0] != LongReport || buf[1] != DirectDevice) continue;
            if (buf[2] == 0xFF && buf[3] == featureIndex) return null;   // HID++ 2.0 error
            if (buf[2] == featureIndex && buf[3] == (byte)((function << 4) | SoftwareId)) return buf.ToArray();
        }
        return null;
    }

    /// The Logitech HID++ long-report collection that belongs to this Bluetooth address.
    private static HidDevice? FindEndpoint(string digits)
    {
        foreach (var d in DeviceList.Local.GetHidDevices(LogitechVendor))
        {
            try
            {
                if (d.GetMaxOutputReportLength() < 20) continue;
                if (!IsVendorCollection(d)) continue;
                if (BelongsTo(d.DevicePath, digits)) return d;
            }
            catch (Exception ex)
            {
                Log.Debug("hidpp", $"skipping HID interface ({ex.Message})");
            }
        }
        return null;
    }

    private static bool IsVendorCollection(HidDevice d) =>
        d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Any(u => (u >> 16) >= 0xFF00));

    /// LE collections carry the address in their path; for Classic, the collection's parent is the
    /// device's BTHENUM HID node, whose instance ID ends with the address.
    private static bool BelongsTo(string devicePath, string digits)
    {
        if (devicePath.ToUpperInvariant().Contains(digits)) return true;
        var instanceId = InstanceIdFromPath(devicePath);
        if (instanceId is null) return false;
        if (CM_Locate_DevNodeW(out var node, instanceId, 0) != 0) return false;
        if (CM_Get_Parent(out var parent, node, 0) != 0) return false;
        var buf = new char[512];
        if (CM_Get_Device_IDW(parent, buf, buf.Length, 0) != 0) return false;
        return new string(buf).TrimEnd('\0').ToUpperInvariant().Contains(digits);
    }

    /// \\?\hid#{...}_vid&0002046d_pid&b342&col03#9&2b3&0&0002#{4d1e55b2-...} -> hid\{...}…\9&2b3&0&0002
    private static string? InstanceIdFromPath(string path)
    {
        var p = path.StartsWith(@"\\?\") ? path[4..] : path;
        var lastHash = p.LastIndexOf('#');
        if (lastHash < 0) return null;
        return p[..lastHash].Replace('#', '\\');
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, int bufferLen, int flags);
}
