import Foundation
import IOKit
import IOKit.hid

/// `HostSwitcher` for ZMK keyboards running the AutoSwitch firmware module
/// (github.com/casab/zmk-autoswitch; protocol in `ZmkHostProtocol`; SPECIFICATION.md §5a type 5).
/// The keyboard's "channels" are its Bluetooth profiles (`BT_SEL 0..n`). Mirrors Windows'
/// `ZmkHostSwitcher`.
///
/// Endpoint: the raw HID collection added by zzeneg/zmk-raw-hid (usage page 0xFF60, usage 0x61).
/// Over Bluetooth LE macOS publishes it as an `IOHIDDevice` of its own (`Transport = "Bluetooth Low
/// Energy"`), separate from the keyboard's, with no `SerialNumber` but `DeviceAddress =
/// "e7-99-a2-72-a6-76"` (seen on the user's Reviung41, 2026-11-14). Reports are 32 bytes without a
/// report ID: output report 0 as is, replies through the input report callback on the main run
/// loop. Any vendor ID (ZMK boards use many). Calls are serialized.
@MainActor
final class ZmkHostSwitcher: HostSwitcher {
    private var inUse = false
    private var openFailureLogged = Set<String>()

    func channels(_ address: String) async throws -> HostChannels? {
        try await withSession(address) { session in await session.readChannels() }
    }

    func switchTo(_ address: String, channel: Int) async throws {
        let status: UInt8? = try await withSession(address) { session in
            try await session.request(ZmkHostProtocol.select, [UInt8(channel)]).map(ZmkHostProtocol.status)
        }
        guard let status else {
            throw BTError.classified(
                .deviceNotFound, "the keyboard's AutoSwitch channel isn't available (not connected to this Mac?)")
        }
        guard status == ZmkHostProtocol.statusOK else {
            throw BTError.classified(.unknown, "the keyboard refused profile \(channel + 1) (status \(status))")
        }
        Log.bluetooth.debug("zmk \(address): select profile \(channel) acknowledged")
    }

    @discardableResult
    func markCurrentChannel(_ address: String) async throws -> Int? {
        try await withSession(address) { session -> Int? in
            guard let ch = await session.readChannels(), ch.marks[ch.current] != HostChannels.autoSwitchMark,
                let reply = try await session.request(
                    ZmkHostProtocol.setMark, [UInt8(ch.current), HostChannels.autoSwitchMark]),
                ZmkHostProtocol.status(reply) == ZmkHostProtocol.statusOK
            else { return nil }
            Log.bluetooth.debug("zmk \(address): set mark (profile \(ch.current), 0xA5)")
            return ch.current
        }
    }

    // MARK: - Sessions

    /// Find the keyboard's raw HID endpoint and run `work`; nil when there is none (not connected
    /// here, or firmware without the module).
    private func withSession<T>(_ address: String, _ work: (ZmkSession) async throws -> T?) async throws -> T? {
        while inUse { try await Task.sleep(nanoseconds: 20_000_000) }
        inUse = true
        defer { inUse = false }

        let key = Self.normalize(address)
        guard let device = Self.findEndpoint(key) else { return nil }
        let session = ZmkSession(device: device)
        let rc = session.open()
        guard rc == kIOReturnSuccess else {
            if openFailureLogged.insert(key).inserted {
                Log.bluetooth.warn(
                    "zmk \(address): cannot open the keyboard's AutoSwitch interface (\(BTCode.describe(rc)))")
            }
            return nil
        }
        openFailureLogged.remove(key)
        defer { session.close() }
        return try await work(session)
    }

    static func normalize(_ address: String) -> String {
        address.replacingOccurrences(of: ":", with: "").replacingOccurrences(of: "-", with: "").uppercased()
    }

    /// The raw HID (0xFF60/0x61) device whose address is `key`: `DeviceAddress` over LE,
    /// `SerialNumber` as a fallback (how Classic HID devices carry it).
    private static func findEndpoint(_ key: String) -> IOHIDDevice? {
        let manager = IOHIDManagerCreate(kCFAllocatorDefault, IOOptionBits(kIOHIDOptionsTypeNone))
        IOHIDManagerSetDeviceMatching(
            manager,
            [
                kIOHIDPrimaryUsagePageKey: ZmkHostProtocol.usagePage, kIOHIDPrimaryUsageKey: ZmkHostProtocol.usage,
            ] as CFDictionary)
        let devices = (IOHIDManagerCopyDevices(manager) as? Set<IOHIDDevice>) ?? []
        return devices.first { device in
            let maxOut = (IOHIDDeviceGetProperty(device, kIOHIDMaxOutputReportSizeKey as CFString) as? Int) ?? 0
            return maxOut >= ZmkHostProtocol.reportSize && addresses(of: device).contains(key)
        }
    }

    private static func addresses(of device: IOHIDDevice) -> [String] {
        ["DeviceAddress", kIOHIDSerialNumberKey].compactMap {
            (IOHIDDeviceGetProperty(device, $0 as CFString) as? String).map(normalize)
        }
    }
}

/// One open conversation with the keyboard's raw HID interface: send a request, await its reply.
@MainActor
private final class ZmkSession {
    private static let replyTimeout: Duration = .milliseconds(1500)

    let device: IOHIDDevice
    private let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: 64)
    private var context: Unmanaged<ZmkSession>?
    private var waiting: (command: UInt8, continuation: CheckedContinuation<[UInt8]?, Never>)?

    init(device: IOHIDDevice) { self.device = device }

    func open() -> IOReturn {
        let rc = IOHIDDeviceOpen(device, IOOptionBits(kIOHIDOptionsTypeNone))
        guard rc == kIOReturnSuccess else { return rc }
        let ctx = Unmanaged.passRetained(self)
        context = ctx
        IOHIDDeviceRegisterInputReportCallback(
            device, buffer, 64,
            { ctx, _, _, _, _, report, length in
                guard let ctx else { return }
                let bytes = Array(UnsafeBufferPointer(start: report, count: length))
                let session = Unmanaged<ZmkSession>.fromOpaque(ctx).takeUnretainedValue()
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

    /// Send a request and wait for its reply (32 bytes). nil on timeout.
    func request(_ command: UInt8, _ args: [UInt8] = []) async throws -> [UInt8]? {
        resume(nil)
        let report = ZmkHostProtocol.request(command, args)
        let rc = IOHIDDeviceSetReport(device, kIOHIDReportTypeOutput, 0, report, report.count)
        guard rc == kIOReturnSuccess else { throw BTError.connectFailed(rc) }
        return await withCheckedContinuation { continuation in
            waiting = (command, continuation)
            Task { @MainActor [weak self] in
                try? await Task.sleep(for: Self.replyTimeout)
                self?.resume(nil)
            }
        }
    }

    func readChannels() async -> HostChannels? {
        do {
            return try await request(ZmkHostProtocol.getInfo).flatMap(ZmkHostProtocol.parseInfo)
        } catch {
            Log.bluetooth.debug("zmk: GET_INFO failed: \(error)")
            return nil
        }
    }

    private func received(_ bytes: [UInt8]) {
        guard let waiting, ZmkHostProtocol.isReply(bytes, to: waiting.command) else { return }
        resume(bytes)
    }

    private func resume(_ value: [UInt8]?) {
        guard let pending = waiting else { return }
        waiting = nil
        pending.continuation.resume(returning: value)
    }
}
