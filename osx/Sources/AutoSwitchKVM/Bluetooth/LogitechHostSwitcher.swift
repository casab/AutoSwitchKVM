import Foundation
import IOKit
import IOKit.hid

/// `HostSwitcher` over Logitech HID++ 2.0, feature 0x1814 "Change Host" (SPECIFICATION.md §5a
/// type 5), for Easy-Switch devices connected straight to this Mac over Bluetooth (device index
/// 0xFF). Mirrors Windows' `LogitechHostSwitcher`.
///
///   [0] getHostInfo() -> nbHost, currHost
///   [1] setCurrentHost(host)          no reply: the device leaves this Mac
///   [2] getCookies() -> one byte per host
///   [3] setCookie(host, cookie)       AutoSwitch marks its own channel with 0xA5
///
/// On macOS a Bluetooth HID device is one `IOHIDDevice` whose `SerialNumber` is its Bluetooth
/// address; the HID++ collection (usage page 0xFF00) is part of it and takes 20-byte output
/// report 0x11. Verified on a K380 (2026-11-13): feature index 7, version 1, 3 channels.
/// Replies arrive through an input-report callback on the main run loop, so waiting never blocks
/// the main thread. Calls are serialized: the engine and Settings may ask at the same time.
@MainActor
final class LogitechHostSwitcher: HostSwitcher {
    private static let vendor = 0x046D
    private static let changeHostFeature: (UInt8, UInt8) = (0x18, 0x14)
    private static let replyTimeout: Duration = .milliseconds(1500)

    /// Change Host feature index per normalized address; 0 = the device doesn't have it.
    private var featureIndex: [String: UInt8] = [:]
    private var inUse = false
    private var permissionReported = false

    func channels(_ address: String) async throws -> HostChannels? {
        try await withSession(address) { session, fi in try await session.readChannels(fi) }
    }

    func switchTo(_ address: String, channel: Int) async throws {
        let sent: Bool? = try await withSession(address) { session, fi in
            try session.send(fi, function: 1, [UInt8(channel)])  // setCurrentHost: no reply expected
            Log.bluetooth.debug("hid++ \(address): setCurrentHost(\(channel)) sent")
            return true
        }
        guard sent == true else {
            throw BTError.classified(
                .deviceNotFound, "the device's Logitech control channel isn't available (not connected to this Mac?)")
        }
    }

    @discardableResult
    func markCurrentChannel(_ address: String) async throws -> Int? {
        try await withSession(address) { session, fi -> Int? in
            guard let ch = try await session.readChannels(fi), ch.current < ch.marks.count,
                ch.marks[ch.current] != HostChannels.autoSwitchMark
            else { return nil }
            _ = try await session.request(fi, function: 3, [UInt8(ch.current), HostChannels.autoSwitchMark])
            Log.bluetooth.debug("hid++ \(address): setCookie(\(ch.current), 0xA5)")
            return ch.current
        }
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

    /// The Logitech Bluetooth HID device with this address that has a HID++ (0xFF00) collection.
    private static func findEndpoint(_ key: String) -> IOHIDDevice? {
        let manager = IOHIDManagerCreate(kCFAllocatorDefault, IOOptionBits(kIOHIDOptionsTypeNone))
        IOHIDManagerSetDeviceMatching(
            manager, [kIOHIDVendorIDKey: vendor, kIOHIDTransportKey: "Bluetooth"] as CFDictionary)
        let devices = (IOHIDManagerCopyDevices(manager) as? Set<IOHIDDevice>) ?? []
        return devices.first { device in
            let serial = IOHIDDeviceGetProperty(device, kIOHIDSerialNumberKey as CFString) as? String ?? ""
            let maxOut = (IOHIDDeviceGetProperty(device, kIOHIDMaxOutputReportSizeKey as CFString) as? Int) ?? 0
            let pairs = IOHIDDeviceGetProperty(device, kIOHIDDeviceUsagePairsKey as CFString) as? [[String: Any]] ?? []
            let vendorCollection = pairs.contains { (($0[kIOHIDDeviceUsagePageKey] as? Int) ?? 0) >= 0xFF00 }
            return normalize(serial) == key && maxOut >= 20 && vendorCollection
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
