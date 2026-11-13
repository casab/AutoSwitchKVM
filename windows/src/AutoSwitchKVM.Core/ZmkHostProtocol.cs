namespace AutoSwitchKVM.Core;

/// The AutoSwitch host channel protocol (v1) spoken by the AutoSwitch ZMK module
/// (github.com/casab/zmk-autoswitch; SPECIFICATION.md §5a type 5). It runs over the keyboard's raw HID
/// interface (zzeneg/zmk-raw-hid: usage page 0xFF60, usage 0x61, 32-byte reports, no report ID).
///
///   byte 0..1  'A' 'S'   magic      byte 2  version (1)      byte 3  command (reply: | 0x80)
///   GET_INFO (1)                 -> status, profile count, active profile, paired lo, paired hi, marks…
///   SELECT (2) profile           -> status (the keyboard switches ~100 ms after replying)
///   SET_MARK (3) profile, value  -> status
/// Status: 0 OK, 1 invalid argument, 2 unsupported.
public static class ZmkHostProtocol
{
    public const byte Magic0 = (byte)'A';
    public const byte Magic1 = (byte)'S';
    public const byte Version = 1;
    public const byte GetInfo = 0x01;
    public const byte Select = 0x02;
    public const byte SetMark = 0x03;
    public const byte ReplyFlag = 0x80;
    public const int ReportSize = 32;
    public const ushort UsagePage = 0xFF60;
    public const ushort Usage = 0x61;

    public const byte StatusOk = 0;
    public const byte StatusInvalidArgument = 1;
    public const byte StatusUnsupported = 2;

    /// A 32-byte request report (without the HID report-ID byte).
    public static byte[] Request(byte command, params byte[] args)
    {
        var r = new byte[ReportSize];
        r[0] = Magic0;
        r[1] = Magic1;
        r[2] = Version;
        r[3] = command;
        Array.Copy(args, 0, r, 4, Math.Min(args.Length, ReportSize - 4));
        return r;
    }

    /// Is this report (without report ID) our reply to `command`?
    public static bool IsReplyTo(ReadOnlySpan<byte> report, byte command) =>
        report.Length >= 5 && report[0] == Magic0 && report[1] == Magic1 && report[2] == Version
        && report[3] == (byte)(command | ReplyFlag);

    public static byte StatusOf(ReadOnlySpan<byte> reply) => reply[4];

    /// The channels from a GET_INFO reply, or null if it isn't a successful one. A profile that
    /// isn't paired is reported in Paired (so nobody sends the keyboard to an empty profile).
    public static HostChannels? ParseInfo(ReadOnlySpan<byte> reply)
    {
        if (!IsReplyTo(reply, GetInfo) || reply.Length < 9 || StatusOf(reply) != StatusOk) return null;
        int count = reply[5], active = reply[6];
        if (count == 0 || active >= count || reply.Length < 9 + count) return null;
        var pairedBits = reply[7] | (reply[8] << 8);
        var paired = Enumerable.Range(0, count).Select(i => (pairedBits & (1 << i)) != 0).ToArray();
        var marks = reply.Slice(9, count).ToArray();
        return new HostChannels(count, active, marks) { Paired = paired };
    }
}
