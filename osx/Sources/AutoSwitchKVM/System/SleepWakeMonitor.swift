import AppKit
import Foundation
import IOKit
import IOKit.pwr_mgt

/// Observes system sleep/wake. On sleep we proactively disconnect (so another host can take
/// the device while the Mac is asleep); on wake we re-seed presence and re-evaluate.
///
/// Uses IOKit root-domain power notifications (`IORegisterForSystemPower`) rather than
/// `NSWorkspace.willSleepNotification`: the IOKit path lets us *hold* the sleep transition
/// (by delaying `IOAllowPowerChange`) until the release work has actually run — a fire-and-forget
/// disconnect on the workspace notification races the machine into sleep and the unpair the other
/// host depends on may never happen. Falls back to the workspace notifications (best-effort)
/// if power registration fails.
@MainActor
final class SleepWakeMonitor {
    /// Awaited — bounded by `sleepHoldLimitSeconds` — before the system is allowed to sleep.
    var onWillSleep: (@MainActor () async -> Void)?
    var onDidWake: (() -> Void)?

    /// Maximum time to hold the sleep transition for the release work. The kernel forcibly
    /// proceeds after ~30 s regardless; stay well under that.
    var sleepHoldLimitSeconds: Int = 15

    // IOKit power messages (IOMessage.h iokit_common_msg values; the macros don't import to Swift).
    private static let msgCanSystemSleep: UInt32 = 0xE000_0270  // kIOMessageCanSystemSleep
    private static let msgSystemWillSleep: UInt32 = 0xE000_0280  // kIOMessageSystemWillSleep
    private static let msgSystemHasPoweredOn: UInt32 = 0xE000_0300  // kIOMessageSystemHasPoweredOn

    private var rootPort: io_connect_t = 0
    private var notifyPort: IONotificationPortRef?
    private var notifier: io_object_t = 0
    private var workspaceObservers: [NSObjectProtocol] = []
    /// The in-flight sleep notification token; consumed by whichever finishes first —
    /// the release work or the hold-limit watchdog.
    private var pendingSleepToken: Int?

    func start() {
        guard rootPort == 0, workspaceObservers.isEmpty else { return }

        let refcon = Unmanaged.passUnretained(self).toOpaque()
        var port: IONotificationPortRef?
        var notifierObj: io_object_t = 0
        let root = IORegisterForSystemPower(
            refcon, &port,
            { refcon, _, messageType, messageArgument in
                guard let refcon else { return }
                // Delivered on the main queue (IONotificationPortSetDispatchQueue below).
                MainActor.assumeIsolated {
                    Unmanaged<SleepWakeMonitor>.fromOpaque(refcon).takeUnretainedValue()
                        .handlePowerMessage(messageType, messageArgument)
                }
            }, &notifierObj)

        if root != 0, let port {
            rootPort = root
            notifyPort = port
            notifier = notifierObj
            IONotificationPortSetDispatchQueue(port, DispatchQueue.main)
        } else {
            Log.app.error("IORegisterForSystemPower failed; falling back to workspace sleep notifications")
            startWorkspaceFallback()
        }
    }

    func stop() {
        if rootPort != 0 {
            IODeregisterForSystemPower(&notifier)
            IOServiceClose(rootPort)
            rootPort = 0
        }
        if let port = notifyPort {
            IONotificationPortDestroy(port)
            notifyPort = nil
        }
        let nc = NSWorkspace.shared.notificationCenter
        workspaceObservers.forEach { nc.removeObserver($0) }
        workspaceObservers.removeAll()
    }

    // MARK: - IOKit path

    private func handlePowerMessage(_ messageType: UInt32, _ argument: UnsafeMutableRawPointer?) {
        switch messageType {
        case Self.msgCanSystemSleep:
            // Never veto idle sleep — just acknowledge promptly.
            IOAllowPowerChange(rootPort, Int(bitPattern: argument))

        case Self.msgSystemWillSleep:
            let token = Int(bitPattern: argument)
            guard let handler = onWillSleep else {
                IOAllowPowerChange(rootPort, token)
                return
            }
            // Hold sleep until the release finishes, bounded by a watchdog. On overrun the
            // release keeps running but the machine is allowed to sleep; wake re-seeds anyway.
            pendingSleepToken = token
            let limit = sleepHoldLimitSeconds
            let start = ContinuousClock.now
            Log.app.log("system will sleep: holding sleep (up to \(limit) s) while devices are released")
            Task { @MainActor [weak self] in
                await handler()
                self?.allowPendingSleep(expecting: token, "release finished in \(Elapsed.since(start))")
            }
            Task { @MainActor [weak self] in
                try? await Task.sleep(nanoseconds: UInt64(max(1, limit)) * 1_000_000_000)
                self?.allowPendingSleep(
                    expecting: token,
                    "hold limit of \(limit) s reached with the release still running; it continues during sleep")
            }

        case Self.msgSystemHasPoweredOn:
            Log.app.log("system woke (powered on)")
            onDidWake?()

        default:
            break
        }
    }

    private func allowPendingSleep(expecting token: Int, _ why: String) {
        guard pendingSleepToken == token, rootPort != 0 else { return }
        pendingSleepToken = nil
        if why.hasPrefix("hold limit") {
            Log.app.warn("allowing sleep: \(why)")
        } else {
            Log.app.debug("allowing sleep: \(why)")
        }
        IOAllowPowerChange(rootPort, token)
    }

    // MARK: - Workspace fallback (best-effort, cannot hold sleep)

    private func startWorkspaceFallback() {
        let nc = NSWorkspace.shared.notificationCenter
        workspaceObservers.append(
            nc.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let handler = self?.onWillSleep else { return }
                    Log.app.warn("system will sleep (workspace fallback: sleep cannot be held; release is best-effort)")
                    Task { @MainActor in await handler() }
                }
            })
        workspaceObservers.append(
            nc.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    Log.app.log("system woke (workspace fallback)")
                    self?.onDidWake?()
                }
            })
    }
}
