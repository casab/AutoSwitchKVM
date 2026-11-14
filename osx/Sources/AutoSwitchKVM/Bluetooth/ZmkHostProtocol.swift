import Foundation

/// The AutoSwitch host channel protocol (v1) spoken by the AutoSwitch ZMK module
/// (github.com/casab/zmk-autoswitch; SPECIFICATION.md §5a type 5). It runs over the keyboard's raw
/// HID interface (zzeneg/zmk-raw-hid: usage page 0xFF60, usage 0x61, 32-byte reports, no report
/// ID). Mirrors Windows' `ZmkHostProtocol`.
///
///   byte 0..1  'A' 'S'   magic      byte 2  version (1)      byte 3  command (reply: | 0x80)
///   GET_INFO (1)                 -> status, profile count, active profile, paired lo, paired hi, marks…
///   SELECT (2) profile           -> status (the keyboard switches ~100 ms after replying)
///   SET_MARK (3) profile, value  -> status
/// Status: 0 OK, 1 invalid argument, 2 unsupported.
enum ZmkHostProtocol {
    static let magic: (UInt8, UInt8) = (UInt8(ascii: "A"), UInt8(ascii: "S"))
    static let version: UInt8 = 1
    static let getInfo: UInt8 = 0x01
    static let select: UInt8 = 0x02
    static let setMark: UInt8 = 0x03
    static let replyFlag: UInt8 = 0x80
    static let reportSize = 32
    static let usagePage = 0xFF60
    static let usage = 0x61

    static let statusOK: UInt8 = 0
    static let statusInvalidArgument: UInt8 = 1
    static let statusUnsupported: UInt8 = 2

    /// A 32-byte request report (no report ID).
    static func request(_ command: UInt8, _ args: [UInt8] = []) -> [UInt8] {
        var report = [UInt8](repeating: 0, count: reportSize)
        report[0] = magic.0
        report[1] = magic.1
        report[2] = version
        report[3] = command
        for (i, b) in args.prefix(reportSize - 4).enumerated() { report[4 + i] = b }
        return report
    }

    /// Is this report our reply to `command`?
    static func isReply(_ report: [UInt8], to command: UInt8) -> Bool {
        report.count >= 5 && report[0] == magic.0 && report[1] == magic.1 && report[2] == version
            && report[3] == command | replyFlag
    }

    static func status(_ reply: [UInt8]) -> UInt8 { reply[4] }

    /// The channels from a GET_INFO reply, or nil if it isn't a successful one. A profile that
    /// isn't paired is reported in `paired` (so nobody sends the keyboard to an empty profile).
    static func parseInfo(_ reply: [UInt8]) -> HostChannels? {
        guard isReply(reply, to: getInfo), reply.count >= 9, status(reply) == statusOK else { return nil }
        let count = Int(reply[5])
        let active = Int(reply[6])
        guard count > 0, active < count, reply.count >= 9 + count else { return nil }
        let bits = Int(reply[7]) | Int(reply[8]) << 8
        return HostChannels(
            count: count, current: active, marks: Array(reply[9..<9 + count]),
            paired: (0..<count).map { bits & (1 << $0) != 0 })
    }
}
