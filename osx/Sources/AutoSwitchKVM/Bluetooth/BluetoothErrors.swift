import Foundation

/// How a Bluetooth failure should be retried (SPECIFICATION.md §3 "Failure classification").
/// Mirrors the Windows `BluetoothFailureKind`: the backend classifies, the engine picks the policy.
enum BTFailureKind: String, Sendable {
    /// Not classified: generic hard retry with backoff (and a fresh bond on the next try).
    case unknown
    /// The device answered but refused. For an exclusive-bond device the other host most likely
    /// still holds it: retry soon, don't escalate.
    case deviceBusy
    /// An earlier operation on the radio/device is still running: wait for it.
    case operationInProgress
    /// The device never answered in time (page timeout, our per-call timeout, ...).
    case timeout
    /// Key exchange failed: typically a stale/mismatched link key. Re-pair from scratch.
    case authenticationFailed
    /// The system doesn't know / can't reach the device.
    case deviceNotFound
    /// The local adapter is off, missing, or not ready.
    case adapterUnavailable
    /// The configured address is malformed: retrying can never succeed.
    case invalidAddress

    /// Refusal/busy: retried on a short fixed interval, not counted against `connectRetryMax`.
    var isSoft: Bool { self == .deviceBusy || self == .operationInProgress }

    /// The failure may mean the bond is stale, so the next try re-pairs from scratch.
    var mayBeStaleBond: Bool { self == .authenticationFailed || self == .timeout || self == .unknown }
}

/// Decodes IOBluetooth result codes. IOBluetooth returns either IOKit `IOReturn` values
/// (`0xE00002xx`, e.g. `0xE00002D6` kIOReturnTimeout) or raw Bluetooth HCI error codes (small
/// positive numbers, e.g. 4 = page timeout, 5 = authentication failure). Logged raw codes like
/// `-536870186` were unreadable; this names them and maps them to a `BTFailureKind`.
enum BTCode {
    private struct Entry {
        let name: String
        let meaning: String
        let kind: BTFailureKind
    }

    // IOKit common errors (IOReturn.h, iokit_common_err(0x2xx)).
    private static let ioReturn: [UInt32: Entry] = [
        0xE000_02BC: Entry(name: "kIOReturnError", meaning: "general error", kind: .unknown),
        0xE000_02BE: Entry(name: "kIOReturnNoResources", meaning: "resource shortage", kind: .deviceBusy),
        0xE000_02C0: Entry(name: "kIOReturnNoDevice", meaning: "no such device", kind: .deviceNotFound),
        0xE000_02C1: Entry(name: "kIOReturnNotPrivileged", meaning: "privilege violation", kind: .unknown),
        0xE000_02C2: Entry(name: "kIOReturnBadArgument", meaning: "invalid argument", kind: .unknown),
        0xE000_02C5: Entry(name: "kIOReturnExclusiveAccess", meaning: "device already open", kind: .deviceBusy),
        0xE000_02C7: Entry(name: "kIOReturnUnsupported", meaning: "unsupported", kind: .unknown),
        0xE000_02CD: Entry(name: "kIOReturnNotOpen", meaning: "device not open", kind: .unknown),
        0xE000_02D5: Entry(
            name: "kIOReturnBusy", meaning: "busy (another operation is running)", kind: .operationInProgress),
        0xE000_02D6: Entry(
            name: "kIOReturnTimeout", meaning: "timed out (the device did not answer)", kind: .timeout),
        0xE000_02D7: Entry(name: "kIOReturnOffline", meaning: "offline", kind: .adapterUnavailable),
        0xE000_02D8: Entry(name: "kIOReturnNotReady", meaning: "not ready", kind: .adapterUnavailable),
        0xE000_02D9: Entry(name: "kIOReturnNotAttached", meaning: "not attached", kind: .deviceNotFound),
        0xE000_02E2: Entry(name: "kIOReturnNotPermitted", meaning: "not permitted", kind: .unknown),
        0xE000_02E3: Entry(name: "kIOReturnNoPower", meaning: "no power (adapter off?)", kind: .adapterUnavailable),
        0xE000_02E9: Entry(name: "kIOReturnDeviceError", meaning: "device error", kind: .unknown),
        0xE000_02EB: Entry(name: "kIOReturnAborted", meaning: "aborted", kind: .unknown),
        0xE000_02ED: Entry(name: "kIOReturnNotResponding", meaning: "device not responding", kind: .timeout),
        0xE000_02F0: Entry(name: "kIOReturnNotFound", meaning: "not found", kind: .deviceNotFound),
    ]

    // Bluetooth HCI error codes (Core spec Vol 1 Part F), as IOBluetooth passes them through.
    private static let hci: [UInt8: Entry] = [
        0x02: Entry(name: "unknown connection identifier", meaning: "no such link", kind: .deviceNotFound),
        0x03: Entry(name: "hardware failure", meaning: "controller failure", kind: .adapterUnavailable),
        0x04: Entry(name: "page timeout", meaning: "the device did not answer", kind: .timeout),
        0x05: Entry(name: "authentication failure", meaning: "stale or mismatched key", kind: .authenticationFailed),
        0x06: Entry(
            name: "PIN or key missing", meaning: "the device no longer has our key", kind: .authenticationFailed),
        0x08: Entry(name: "connection timeout", meaning: "link supervision timeout", kind: .timeout),
        0x09: Entry(name: "connection limit exceeded", meaning: "refused", kind: .deviceBusy),
        0x0B: Entry(name: "connection already exists", meaning: "already connected", kind: .operationInProgress),
        0x0C: Entry(
            name: "command disallowed", meaning: "controller busy with another command", kind: .operationInProgress),
        0x0D: Entry(name: "connection rejected (limited resources)", meaning: "refused", kind: .deviceBusy),
        0x0E: Entry(name: "connection rejected (security reasons)", meaning: "refused", kind: .deviceBusy),
        0x0F: Entry(name: "connection rejected (unacceptable address)", meaning: "refused", kind: .deviceBusy),
        0x10: Entry(name: "connection accept timeout", meaning: "the device did not answer", kind: .timeout),
        0x13: Entry(name: "remote user terminated connection", meaning: "the device closed the link", kind: .unknown),
        0x14: Entry(
            name: "remote device terminated (low resources)", meaning: "the device closed the link", kind: .deviceBusy),
        0x15: Entry(
            name: "remote device terminated (power off)", meaning: "the device powered off", kind: .deviceNotFound),
        0x16: Entry(name: "connection terminated by local host", meaning: "this Mac closed the link", kind: .unknown),
        0x17: Entry(name: "repeated attempts", meaning: "the device is throttling pairing attempts", kind: .unknown),
        0x18: Entry(name: "pairing not allowed", meaning: "the device refused pairing", kind: .deviceBusy),
        0x22: Entry(name: "LMP response timeout", meaning: "the device stopped answering", kind: .timeout),
        0x23: Entry(
            name: "LMP transaction collision", meaning: "collided with another procedure", kind: .operationInProgress),
        0x25: Entry(name: "encryption mode not acceptable", meaning: "security mismatch", kind: .authenticationFailed),
        0x26: Entry(name: "link key cannot be changed", meaning: "security mismatch", kind: .authenticationFailed),
        0x2A: Entry(
            name: "different transaction collision", meaning: "collided with another procedure",
            kind: .operationInProgress),
        0x2F: Entry(name: "insufficient security", meaning: "security mismatch", kind: .authenticationFailed),
        0x38: Entry(name: "host busy (pairing)", meaning: "another pairing is running", kind: .operationInProgress),
        0x3A: Entry(name: "controller busy", meaning: "controller busy", kind: .operationInProgress),
        0x3E: Entry(name: "connection failed to be established", meaning: "the device did not answer", kind: .timeout),
    ]

    private static func entry(_ code: Int32) -> Entry? {
        if code > 0, code <= 0xFF { return hci[UInt8(code)] }
        return ioReturn[UInt32(bitPattern: code)]
    }

    static func kind(_ code: Int32) -> BTFailureKind {
        entry(code)?.kind ?? .unknown
    }

    /// e.g. "0xE00002D6 kIOReturnTimeout (timed out (the device did not answer))", "HCI 0x05
    /// authentication failure (stale or mismatched key)", or "0xFFFFFFFF (unrecognized code)".
    static func describe(_ code: Int32) -> String {
        if code > 0, code <= 0xFF {
            let hex = String(format: "HCI 0x%02X", code)
            guard let e = hci[UInt8(code)] else { return "\(hex) (unrecognized HCI code)" }
            return "\(hex) \(e.name) (\(e.meaning))"
        }
        let hex = String(format: "0x%08X", UInt32(bitPattern: code))
        guard let e = ioReturn[UInt32(bitPattern: code)] else { return "\(hex) (unrecognized code)" }
        return "\(hex) \(e.name) (\(e.meaning))"
    }
}

enum BTError: Error, CustomStringConvertible {
    case invalidAddress(String)
    /// `openConnection()` returned this code.
    case connectFailed(Int32)
    /// `closeConnection()` returned this code.
    case disconnectFailed(Int32)
    /// Pairing failed at `step`; `code` is the IOReturn/HCI status when the stack reported one.
    case pairFailed(String, code: Int32? = nil)
    /// An in-flight pairing was stopped (e.g. a release for the same device superseded it).
    case pairCancelled(String)
    case unpairUnavailable
    /// The bond survived the removal attempt.
    case unpairFailed(String)
    /// The per-call timeout expired before the operation answered.
    case timeout(seconds: Int)
    /// A failure whose kind was determined without a raw code (also used by test doubles).
    case classified(BTFailureKind, String)

    var kind: BTFailureKind {
        switch self {
        case .invalidAddress: return .invalidAddress
        case .connectFailed(let c), .disconnectFailed(let c): return BTCode.kind(c)
        case .pairFailed(_, let code): return code.map(BTCode.kind) ?? .unknown
        case .pairCancelled: return .unknown
        case .unpairUnavailable, .unpairFailed: return .unknown
        case .timeout: return .timeout
        case .classified(let kind, _): return kind
        }
    }

    var description: String {
        switch self {
        case .invalidAddress(let a): return "invalid Bluetooth address '\(a)'"
        case .connectFailed(let c): return "openConnection failed: \(BTCode.describe(c))"
        case .disconnectFailed(let c): return "closeConnection failed: \(BTCode.describe(c))"
        case .pairFailed(let step, let code):
            return code.map { "pairing failed (\(step)): \(BTCode.describe($0))" } ?? "pairing failed (\(step))"
        case .pairCancelled(let why): return "pairing cancelled: \(why)"
        case .unpairUnavailable: return "unpair API (-[IOBluetoothDevice remove]) unavailable on this macOS"
        case .unpairFailed(let why): return "unpair failed: \(why)"
        case .timeout(let s): return "no response within \(s) s"
        case .classified(_, let message): return message
        }
    }
}
