import Foundation
import AppKit

/// Observes system sleep/wake. On sleep we proactively disconnect (so another host can take
/// the device while the Mac is asleep); on wake we re-seed presence and re-evaluate.
@MainActor
final class SleepWakeMonitor {
    var onWillSleep: (() -> Void)?
    var onDidWake: (() -> Void)?

    private var observers: [NSObjectProtocol] = []

    func start() {
        let nc = NSWorkspace.shared.notificationCenter
        observers.append(nc.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.onWillSleep?() }
        })
        observers.append(nc.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.onDidWake?() }
        })
    }

    func stop() {
        let nc = NSWorkspace.shared.notificationCenter
        observers.forEach { nc.removeObserver($0) }
        observers.removeAll()
    }
}
