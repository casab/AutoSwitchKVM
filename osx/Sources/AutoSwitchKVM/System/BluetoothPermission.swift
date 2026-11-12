import CoreBluetooth
import Foundation

/// The app's Bluetooth privacy permission (TCC). IOBluetooth needs it on macOS 11+, and when it is
/// denied the IOBluetooth calls fail quietly (no host controller, empty device lists, failed
/// connects) — easy to misread as a device problem. Ad-hoc signed rebuilds can reset the grant, so
/// the app logs this at launch. Reading `CBManager.authorization` never shows a prompt.
enum BluetoothPermission {
    case allowed, denied, restricted, notDetermined, unknown(Int)

    static var current: BluetoothPermission {
        switch CBManager.authorization {
        case .allowedAlways: return .allowed
        case .denied: return .denied
        case .restricted: return .restricted
        case .notDetermined: return .notDetermined
        @unknown default: return .unknown(CBManager.authorization.rawValue)
        }
    }

    /// True when Bluetooth calls are expected to fail for this app.
    var isProblem: Bool {
        switch self {
        case .denied, .restricted: return true
        default: return false
        }
    }

    var description: String {
        switch self {
        case .allowed: return "allowed"
        case .denied:
            return "DENIED: Bluetooth calls will fail. Allow it in System Settings ▸ Privacy & Security ▸ Bluetooth"
        case .restricted: return "RESTRICTED by a system policy: Bluetooth calls will fail"
        case .notDetermined: return "not determined yet (macOS asks on first Bluetooth use)"
        case .unknown(let raw): return "unknown state (\(raw))"
        }
    }
}
