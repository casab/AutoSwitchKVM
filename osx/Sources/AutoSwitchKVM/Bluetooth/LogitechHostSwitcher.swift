import Foundation
import IOKit
import IOKit.hid
import SystemConfiguration

/// `HostSwitcher` over HID++ 2.0 feature 0x1814 "Change Host" (SPECIFICATION.md §5a type 5), for
/// devices connected straight to this Mac over Bluetooth (device index 0xFF): Logitech Easy-Switch
/// devices, and other vendors' devices with the same HID++ layout (the ZMK AutoSwitch module v2,
/// agent-notes/2026-11-14-osx-zmk-hidpp-proposal.md). Mirrors Windows' `LogitechHostSwitcher`.
///
///   [0] getHostInfo() -> nbHost, currHost
///   [1] setCurrentHost(host)          no reply: the device leaves this Mac
///   [2] getCookies() -> one byte per host
///   [3] setCookie(host, cookie)       AutoSwitch marks its own channel with 0xA5
///
/// On macOS a Classic Bluetooth HID device is one `IOHIDDevice` whose `SerialNumber` is its
/// address; the HID++ collection (usage page 0xFF00) is part of it and takes 20-byte output report
/// 0x11. Verified on a K380 (2026-11-13): feature index 7, version 1, 3 channels. An LE device's
/// extra HID service is an `IOHIDDevice` of its own, with the address in `DeviceAddress`.
/// Replies arrive through an input-report callback on the main run loop, so waiting never blocks
/// the main thread. Calls are serialized: the engine and Settings may ask at the same time.
@MainActor
final class LogitechHostSwitcher: HostSwitcher {
    nonisolated static let logitech = 0x046D
    /// HID++ collection of Logitech's BLE devices; any vendor that uses it is spoken to.
    nonisolated static let hidppUsagePage = 0xFF43
    nonisolated static let hidppUsage = 0x0202
    private static let changeHostFeature: (UInt8, UInt8) = (0x18, 0x14)
    private static let hostsInfoFeature: (UInt8, UInt8) = (0x18, 0x15)
    /// Longest name AutoSwitch writes when the device doesn't say (0x1815 name max length 0).
    private static let defaultNameMax = 32

    /// Change Host feature index per normalized address; 0 = the device doesn't have it.
    private var featureIndex: [String: UInt8] = [:]
    /// Hosts Info (0x1815) feature index per normalized address; 0 = the device doesn't have it.
    private var hostsInfoIndex: [String: UInt8] = [:]
    private var inUse = false
    private var permissionReported = false

    /// Channels, with pairing per channel from Hosts Info when the device has it (so the engine
    /// never sends it to an empty channel).
    func channels(_ address: String) async throws -> HostChannels? {
        try await withSession(address) { session, fi in
            guard var ch = try await session.readChannels(fi) else { return nil }
            if let hi = try await hostsInfo(session, address), hi != 0 {
                ch.paired = try await Self.readPaired(session, hi, count: ch.count)
            }
            return ch
        }
    }

    func switchTo(_ address: String, channel: Int) async throws {
        let sent: Bool? = try await withSession(address) { session, fi in
            try session.send(fi, function: 1, [UInt8(channel)])  // setCurrentHost: no reply expected
            Log.bluetooth.debug("hid++ \(address): setCurrentHost(\(channel)) sent")
            return true
        }
        guard sent == true else {
            throw BTError.classified(
                .deviceNotFound, "the device's HID++ control channel isn't available (not connected to this Mac?)")
        }
    }

    /// Mark this Mac's channel (cookie 0xA5) and, when the device keeps names and this channel has
    /// none yet, write this Mac's name there (a ZMK keyboard gets names only this way; Logitech
    /// devices name their channels themselves, and those names are left alone).
    @discardableResult
    func markCurrentChannel(_ address: String) async throws -> Int? {
        try await withSession(address) { session, fi -> Int? in
            guard let ch = try await session.readChannels(fi), ch.current < ch.marks.count else { return nil }
            var marked: Int?
            if ch.marks[ch.current] != HostChannels.autoSwitchMark {
                _ = try await session.request(fi, function: 3, [UInt8(ch.current), HostChannels.autoSwitchMark])
                Log.bluetooth.debug("hid++ \(address): setCookie(\(ch.current), 0xA5)")
                marked = ch.current
            }
            if let hi = try await hostsInfo(session, address), hi != 0 {
                try await nameChannelIfUnnamed(session, hi, channel: ch.current, address: address)
            }
            return marked
        }
    }

    /// HID++ 0x1815 "Hosts Info", layout as in Solaar (same as Windows):
    ///   [0] getFeatureInfo() -> caps (bit 0 = names readable, bit 1 = writable), ?, hosts, current
    ///   [1] getHostInfo(host) -> host, status (0 = not paired), bus, pages, name length, name max
    ///   [3] getHostName(host, offset) -> host, offset, up to 14 name bytes
    ///   [4] setHostName(host, offset, up to 14 name bytes)
    /// Verified on a K380 from Windows: feature index 8, v0, names for all 3 channels.
    func channelNames(_ address: String) async throws -> [String?]? {
        try await withSession(address) { session, _ -> [String?]? in
            guard let hi = try await hostsInfo(session, address), hi != 0,
                let info = try await session.request(hi, function: 0, []), info[4] & 0x01 != 0
            else {
                return nil  // no names on this device
            }
            let hosts = min(Int(info[6]), 8)
            var names = [String?](repeating: nil, count: hosts)
            for host in 0..<hosts {
                guard let hostInfo = try await session.request(hi, function: 1, [UInt8(host)]), hostInfo[5] != 0 else {
                    continue  // channel not paired
                }
                let length = min(Int(hostInfo[8]), 64)
                var bytes: [UInt8] = []
                var offset = 0
                while offset < length {
                    guard let piece = try await session.request(hi, function: 3, [UInt8(host), UInt8(offset)]) else {
                        break
                    }
                    bytes += piece.dropFirst(6).prefix(min(14, length - offset))
                    offset += 14
                }
                names[host] = HostChannels.decodeName(bytes, length: length)
            }
            return names
        }
    }

    // MARK: - Hosts Info

    /// The Hosts Info feature index (cached per device); 0 = absent, nil = no answer.
    private func hostsInfo(_ session: HIDPPSession, _ address: String) async throws -> UInt8? {
        let key = Self.normalize(address)
        if let cached = hostsInfoIndex[key] { return cached }
        let (hi8, lo8) = Self.hostsInfoFeature
        guard let reply = try await session.request(0x00, function: 0, [hi8, lo8]) else { return nil }
        hostsInfoIndex[key] = reply[4]
        Log.bluetooth.debug(
            "hid++ \(address): Hosts Info feature \(reply[4] == 0 ? "not present" : "at index \(reply[4])")")
        return reply[4]
    }

    /// Pairing per channel from `getHostInfo(n)` status; nil if any channel doesn't answer
    /// (unknown is safer than a wrong "unpaired").
    private static func readPaired(_ session: HIDPPSession, _ hi: UInt8, count: Int) async throws -> [Bool]? {
        var paired: [Bool] = []
        for host in 0..<count {
            guard let info = try await session.request(hi, function: 1, [UInt8(host)]) else { return nil }
            paired.append(info[5] != 0)
        }
        return paired
    }

    private func nameChannelIfUnnamed(
        _ session: HIDPPSession, _ hi: UInt8, channel: Int, address: String
    ) async throws {
        // Caps bit 1 = names writable (Solaar gates set_host_name on it; the ZMK module reports 0x03).
        guard let info = try await session.request(hi, function: 0, []), info[4] & 0x02 != 0,
            let hostInfo = try await session.request(hi, function: 1, [UInt8(channel)]),
            hostInfo[5] != 0, hostInfo[8] == 0
        else { return }  // names not writable, channel not paired, or already named
        let max = hostInfo[9] == 0 ? Self.defaultNameMax : Int(hostInfo[9])
        let name = Self.encodeName(Self.computerName(), maxBytes: max)
        guard !name.isEmpty else { return }
        for (offset, piece) in Self.namePieces(name) {
            guard try await session.request(hi, function: 4, [UInt8(channel), UInt8(offset)] + piece) != nil else {
                Log.bluetooth.debug("hid++ \(address): setHostName(\(channel), offset \(offset)) got no reply")
                return
            }
        }
        Log.app.log(
            "\(address): named channel \(channel + 1) \"\(String(decoding: name, as: UTF8.self))\" on the device")
    }

    /// This Mac's name, as in System Settings ▸ General ▸ Sharing.
    nonisolated static func computerName() -> String {
        (SCDynamicStoreCopyComputerName(nil, nil) as String?) ?? Host.current().localizedName ?? "Mac"
    }

    /// UTF-8 bytes of `name`, cut at a character boundary to fit `maxBytes`.
    nonisolated static func encodeName(_ name: String, maxBytes: Int) -> [UInt8] {
        var bytes: [UInt8] = []
        for character in name.trimmingCharacters(in: .whitespacesAndNewlines) {
            let utf8 = Array(String(character).utf8)
            guard bytes.count + utf8.count <= maxBytes else { break }
            bytes += utf8
        }
        return bytes
    }

    /// `setHostName` pieces: (offset, up to 14 bytes).
    nonisolated static func namePieces(_ bytes: [UInt8]) -> [(Int, [UInt8])] {
        stride(from: 0, to: bytes.count, by: 14).map { ($0, Array(bytes[$0..<min($0 + 14, bytes.count)])) }
    }

    // MARK: - Sessions

    /// Find the device's HID++ endpoint, look up the Change Host feature (cached), run `work`.
    /// nil when the device isn't connected here or has no Change Host feature.
    private func withSession<T>(
        _ address: String, _ work: (HIDPPSession, UInt8) async throws -> T?
    ) async throws -> T? {
        while inUse { try await Task.sleep(nanoseconds: 20_000_000) }
        inUse = true
        defer { inUse = false }

        let key = Self.normalize(address)
        guard let device = Self.findEndpoint(key) else { return nil }
        let session = HIDPPSession(device: device)
        let rc = session.open()
        guard rc == kIOReturnSuccess else {
            reportOpenFailure(address, rc)
            return nil
        }
        defer { session.close() }

        let fi: UInt8
        if let cached = featureIndex[key] {
            fi = cached
        } else {
            let (hi, lo) = Self.changeHostFeature
            guard let reply = try await session.request(0x00, function: 0, [hi, lo]) else { return nil }
            fi = reply[4]
            featureIndex[key] = fi
            Log.bluetooth.debug("hid++ \(address): Change Host feature \(fi == 0 ? "not present" : "at index \(fi)")")
        }
        guard fi != 0 else { return nil }
        return try await work(session, fi)
    }

    private func reportOpenFailure(_ address: String, _ rc: IOReturn) {
        if rc == kIOReturnNotPermitted {
            if !permissionReported {
                permissionReported = true
                Log.app.warn(
                    "Logitech channel switching needs the Input Monitoring permission: System Settings ▸ "
                        + "Privacy & Security ▸ Input Monitoring ▸ AutoSwitch KVM")
                _ = IOHIDRequestAccess(kIOHIDRequestTypeListenEvent)
            }
        } else {
            Log.bluetooth.debug("hid++ \(address): cannot open the HID device (\(BTCode.describe(rc)))")
        }
    }

    private static func normalize(_ address: String) -> String {
        address.replacingOccurrences(of: ":", with: "").replacingOccurrences(of: "-", with: "").uppercased()
    }

    /// Whether a HID device is an HID++ endpoint AutoSwitch may talk to: a Logitech device with a
    /// vendor collection (0xFF00 on Classic), or any vendor's device with Logitech's HID++
    /// collection (0xFF43/0x0202). Other vendors' vendor pages are never probed: the Magic
    /// Trackpad has a 0xFF00 collection of its own.
    nonisolated static func isHIDPPEndpoint(vendorID: Int, usagePairs: [(page: Int, usage: Int)], maxOutput: Int)
        -> Bool
    {
        guard maxOutput >= 20 else { return false }
        if vendorID == logitech { return usagePairs.contains { $0.page >= 0xFF00 } }
        return usagePairs.contains { $0.page == hidppUsagePage && $0.usage == hidppUsage }
    }

    /// The Bluetooth HID device with this address that is an HID++ endpoint. Over Classic the
    /// address is the `SerialNumber`; over LE it's `DeviceAddress`.
    private static func findEndpoint(_ key: String) -> IOHIDDevice? {
        let manager = IOHIDManagerCreate(kCFAllocatorDefault, IOOptionBits(kIOHIDOptionsTypeNone))
        IOHIDManagerSetDeviceMatchingMultiple(
            manager,
            [
                [kIOHIDVendorIDKey: logitech],
                [kIOHIDDeviceUsagePageKey: hidppUsagePage, kIOHIDDeviceUsageKey: hidppUsage],
            ] as CFArray)
        let devices = (IOHIDManagerCopyDevices(manager) as? Set<IOHIDDevice>) ?? []
        return devices.first { device in
            let addresses = ["DeviceAddress", kIOHIDSerialNumberKey].compactMap {
                (IOHIDDeviceGetProperty(device, $0 as CFString) as? String).map(normalize)
            }
            guard addresses.contains(key) else { return false }
            let vendorID = (IOHIDDeviceGetProperty(device, kIOHIDVendorIDKey as CFString) as? Int) ?? 0
            let maxOut = (IOHIDDeviceGetProperty(device, kIOHIDMaxOutputReportSizeKey as CFString) as? Int) ?? 0
            let pairs =
                (IOHIDDeviceGetProperty(device, kIOHIDDeviceUsagePairsKey as CFString) as? [[String: Any]] ?? [])
                .map {
                    (page: ($0[kIOHIDDeviceUsagePageKey] as? Int) ?? 0, usage: ($0[kIOHIDDeviceUsageKey] as? Int) ?? 0)
                }
            return isHIDPPEndpoint(vendorID: vendorID, usagePairs: pairs, maxOutput: maxOut)
        }
    }
}

/// One open HID++ conversation with a device: send long reports, await the matching reply.
@MainActor
private final class HIDPPSession {
    private static let longReport: UInt8 = 0x11
    private static let directDevice: UInt8 = 0xFF
    private static let softwareID: UInt8 = 0x0B  // ours; Logitech software uses other IDs (same as Windows)

    let device: IOHIDDevice
    private let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: 64)
    private var context: Unmanaged<HIDPPSession>?
    /// The reply being waited for: (feature index, function|softwareID byte) and its continuation.
    private var waiting: (featureIndex: UInt8, functionByte: UInt8, continuation: CheckedContinuation<[UInt8]?, Never>)?

    init(device: IOHIDDevice) { self.device = device }

    func open() -> IOReturn {
        let rc = IOHIDDeviceOpen(device, IOOptionBits(kIOHIDOptionsTypeNone))
        guard rc == kIOReturnSuccess else { return rc }
        let ctx = Unmanaged.passRetained(self)
        context = ctx
        IOHIDDeviceRegisterInputReportCallback(
            device, buffer, 64,
            { ctx, _, _, _, reportID, report, length in
                guard let ctx else { return }
                var bytes = Array(UnsafeBufferPointer(start: report, count: length))
                // Some paths deliver the report without its ID byte; normalize to [id, ...].
                if bytes.first != UInt8(truncatingIfNeeded: reportID) {
                    bytes.insert(UInt8(truncatingIfNeeded: reportID), at: 0)
                }
                let session = Unmanaged<HIDPPSession>.fromOpaque(ctx).takeUnretainedValue()
                MainActor.assumeIsolated { session.received(bytes) }
            }, ctx.toOpaque())
        IOHIDDeviceScheduleWithRunLoop(device, CFRunLoopGetMain(), CFRunLoopMode.defaultMode.rawValue)
        return rc
    }

    func close() {
        resume(nil)
        IOHIDDeviceRegisterInputReportCallback(device, buffer, 64, nil, nil)
        IOHIDDeviceUnscheduleFromRunLoop(device, CFRunLoopGetMain(), CFRunLoopMode.defaultMode.rawValue)
        IOHIDDeviceClose(device, IOOptionBits(kIOHIDOptionsTypeNone))
        context?.release()
        context = nil
        buffer.deallocate()
    }

    func send(_ featureIndex: UInt8, function: UInt8, _ args: [UInt8]) throws {
        var report = [UInt8](repeating: 0, count: 20)
        report[0] = Self.longReport
        report[1] = Self.directDevice
        report[2] = featureIndex
        report[3] = (function << 4) | Self.softwareID
        for (i, b) in args.prefix(16).enumerated() { report[4 + i] = b }
        let rc = IOHIDDeviceSetReport(device, kIOHIDReportTypeOutput, CFIndex(Self.longReport), report, report.count)
        guard rc == kIOReturnSuccess else { throw BTError.connectFailed(rc) }
    }

    /// Send a request and wait for its reply. nil on timeout or a HID++ error reply.
    func request(_ featureIndex: UInt8, function: UInt8, _ args: [UInt8]) async throws -> [UInt8]? {
        resume(nil)
        let functionByte = (function << 4) | Self.softwareID
        try send(featureIndex, function: function, args)
        return await withCheckedContinuation { continuation in
            waiting = (featureIndex, functionByte, continuation)
            Task { @MainActor [weak self] in
                try? await Task.sleep(for: .milliseconds(1500))
                self?.resume(nil)
            }
        }
    }

    func readChannels(_ fi: UInt8) async throws -> HostChannels? {
        guard let info = try await request(fi, function: 0, []) else { return nil }
        let count = Int(info[4])
        let current = Int(info[5])
        let cookies = try await request(fi, function: 2, [])
        let marks = cookies.map { Array($0.dropFirst(4).prefix(count)) } ?? [UInt8](repeating: 0, count: count)
        return HostChannels(count: count, current: current, marks: marks)
    }

    private func received(_ bytes: [UInt8]) {
        guard let waiting, bytes.count >= 7, bytes[0] == Self.longReport, bytes[1] == Self.directDevice else { return }
        if bytes[2] == 0xFF, bytes[3] == waiting.featureIndex {  // HID++ 2.0 error reply
            Log.bluetooth.debug("hid++ error reply \(bytes.prefix(7).map { String(format: "%02X", $0) })")
            resume(nil)
        } else if bytes[2] == waiting.featureIndex, bytes[3] == waiting.functionByte {
            resume(bytes)
        }
    }

    private func resume(_ value: [UInt8]?) {
        guard let pending = waiting else { return }
        waiting = nil
        pending.continuation.resume(returning: value)
    }
}
