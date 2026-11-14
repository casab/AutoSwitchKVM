import Foundation
import IOBluetooth

/// Collects what macOS knows about paired Bluetooth devices, for the §5a handoff classification.
///
/// The source is `system_profiler SPBluetoothDataType -json` (~0.1 s): bluetoothd reports vendor
/// ID, product ID and minor type ("Keyboard", "Gamepad", "Headset") for Classic and LE devices
/// alike, without paging the device. IOBluetooth's own API only exposes the Device ID record
/// when an SDP query has been cached, and it doesn't list LE devices. When a device is missing
/// from that report, IOBluetooth's class of device still gives its kind.
@MainActor
final class DeviceIdentityReader {
    private var cache: [String: DeviceIdentity] = [:]
    private var cacheTime: Date?
    private static let cacheLifetime: TimeInterval = 30

    /// Identity of the device with this address. `name` is used when the system has none.
    func identity(address: String, name: String) async -> DeviceIdentity {
        if cacheTime.map({ Date().timeIntervalSince($0) > Self.cacheLifetime }) ?? true {
            await reload()
        }
        let key = Self.normalize(address)
        if var known = cache[key] {
            if known.name.isEmpty { known.name = name }
            if known.kind == .unknown { known.kind = Self.kindFromClassOfDevice(address) }
            return known
        }
        return DeviceIdentity(name: name, kind: Self.kindFromClassOfDevice(address))
    }

    /// Forget the report, e.g. after the user paired something new.
    func invalidate() { cacheTime = nil }

    private func reload() async {
        let started = ContinuousClock.now
        let data = await Self.runSystemProfiler()
        cacheTime = Date()
        guard let data else { return }
        cache = Self.parse(data)
        Log.bluetooth.debug(
            "device identities: \(cache.count) from system_profiler in \(Elapsed.since(started))")
    }

    /// Every device `system_profiler` lists as paired, LE included (IOBluetooth's own list is
    /// Classic-only). Addresses as "e7-99-a2-72-a6-76".
    nonisolated static func systemPairedDevices() async -> [PairedDeviceInfo] {
        guard let data = await runSystemProfiler() else { return [] }
        return parse(data).map { key, identity in
            let pairs = stride(from: 0, to: key.count, by: 2).map {
                key.dropFirst($0).prefix(2)
            }
            return PairedDeviceInfo(name: identity.name, address: pairs.joined(separator: "-").lowercased())
        }.sorted { $0.name < $1.name }
    }

    // MARK: - system_profiler

    nonisolated private static func runSystemProfiler() async -> Data? {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .utility).async {
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/usr/sbin/system_profiler")
                process.arguments = ["SPBluetoothDataType", "-json", "-detailLevel", "basic"]
                let pipe = Pipe()
                process.standardOutput = pipe
                process.standardError = FileHandle.nullDevice
                do {
                    try process.run()
                } catch {
                    Log.bluetooth.warn("device identities: cannot run system_profiler: \(error)")
                    continuation.resume(returning: nil)
                    return
                }
                let data = pipe.fileHandleForReading.readDataToEndOfFile()
                process.waitUntilExit()
                if process.terminationStatus != 0 {
                    Log.bluetooth.warn("device identities: system_profiler exited \(process.terminationStatus)")
                }
                continuation.resume(returning: data.isEmpty ? nil : data)
            }
        }
    }

    /// Parse `system_profiler SPBluetoothDataType -json`: devices sit in arrays of one-key
    /// dictionaries `{ "<name>": { "device_address": "34:88:…", "device_vendorID": "0x046D",
    /// "device_productID": "0xB342", "device_minorType": "Keyboard" } }` under keys like
    /// `device_connected` / `device_not_connected`. Keyed by normalized address.
    nonisolated static func parse(_ data: Data) -> [String: DeviceIdentity] {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
            let sections = root["SPBluetoothDataType"] as? [[String: Any]]
        else {
            Log.bluetooth.warn("device identities: unexpected system_profiler output")
            return [:]
        }
        var result: [String: DeviceIdentity] = [:]
        for section in sections {
            for (key, value) in section where key.hasPrefix("device_") {
                guard let entries = value as? [[String: Any]] else { continue }
                for entry in entries {
                    for (name, info) in entry {
                        guard let info = info as? [String: Any],
                            let address = info["device_address"] as? String
                        else { continue }
                        result[normalize(address)] = DeviceIdentity(
                            name: name,
                            vendorID: hex(info["device_vendorID"]),
                            productID: hex(info["device_productID"]),
                            kind: kind(minorType: info["device_minorType"] as? String))
                    }
                }
            }
        }
        return result
    }

    /// "0x046D" or "0x004C (Apple)" -> 0x046D.
    nonisolated static func hex(_ value: Any?) -> UInt16? {
        guard let text = value as? String else { return nil }
        let token = text.split(separator: " ").first.map(String.init) ?? text
        let digits = token.lowercased().hasPrefix("0x") ? String(token.dropFirst(2)) : token
        return UInt16(digits, radix: 16)
    }

    nonisolated static func kind(minorType: String?) -> DeviceKind {
        guard let t = minorType?.lowercased(), !t.isEmpty else { return .unknown }
        if ["gamepad", "joystick", "controller"].contains(where: t.contains) { return .gameController }
        if ["keyboard", "mouse", "trackpad", "pointing", "digitizer", "combo", "remote"].contains(where: t.contains) {
            return .input
        }
        if ["headset", "headphone", "speaker", "audio", "loudspeaker", "hands-free", "earbud"].contains(
            where: t.contains)
        {
            return .audio
        }
        return .unknown
    }

    // MARK: - IOBluetooth fallback

    /// Kind from the Classic class of device: major 0x05 (peripheral) is input, with minor
    /// subtype 1/2 (joystick/gamepad) a game controller; major 0x04 is audio/video.
    private static func kindFromClassOfDevice(_ address: String) -> DeviceKind {
        let dashed = address.replacingOccurrences(of: ":", with: "-").lowercased()
        guard let device = IOBluetoothDevice(addressString: dashed) else { return .unknown }
        return kind(major: Int(device.deviceClassMajor), minor: Int(device.deviceClassMinor))
    }

    nonisolated static func kind(major: Int, minor: Int) -> DeviceKind {
        switch major {
        case 0x05: return (minor & 0x0F) == 1 || (minor & 0x0F) == 2 ? .gameController : .input
        case 0x04: return .audio
        default: return .unknown
        }
    }

    nonisolated static func normalize(_ address: String) -> String {
        address.replacingOccurrences(of: ":", with: "").replacingOccurrences(of: "-", with: "").uppercased()
    }
}
