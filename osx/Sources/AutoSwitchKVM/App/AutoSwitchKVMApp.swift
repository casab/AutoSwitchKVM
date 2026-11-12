import AppKit
import SwiftUI

@main
struct AutoSwitchKVMApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var controller = AppController.shared

    var body: some Scene {
        MenuBarExtra {
            MenuContentView()
                .environmentObject(controller)
                .environmentObject(controller.store)
                .environmentObject(controller.engine)
                .environmentObject(controller.usb)
        } label: {
            Image(systemName: menuBarSymbol)
        }
        .menuBarExtraStyle(.window)

        Window("AutoSwitch KVM Settings", id: "settings") {
            SettingsView()
                .environmentObject(controller)
                .environmentObject(controller.store)
                .environmentObject(controller.engine)
                .environmentObject(controller.usb)
                .environmentObject(controller.learner)
                .environmentObject(DebugLog.shared)
                .frame(minWidth: 600, minHeight: 480)
                .onAppear { NSApp.activate(ignoringOtherApps: true) }
        }
        .windowResizability(.contentSize)
    }

    private var menuBarSymbol: String {
        if !controller.bluetoothPowered { return "exclamationmark.triangle" }
        if controller.paused { return "pause.rectangle" }
        return controller.sourceActive ? "rectangle.connected.to.line.below" : "rectangle.dashed"
    }
}

/// Hides the Dock icon (menu-bar-only app) and starts the controller.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    /// False when another copy already runs; this one then quits without touching anything.
    private var isPrimary = true

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard SingleInstance.claim() else {
            isPrimary = false
            Log.app.warn("another copy of AutoSwitch KVM is already running; this one quits")
            NSApp.setActivationPolicy(.regular)
            NSApp.activate(ignoringOtherApps: true)
            let alert = NSAlert()
            alert.messageText = "AutoSwitch KVM is already running"
            alert.informativeText =
                "Only one copy can manage your devices at a time. Use the one in the menu bar, or quit it "
                + "before opening this one."
            alert.runModal()
            NSApp.terminate(nil)
            return
        }
        NSApp.setActivationPolicy(.accessory)
        AppController.shared.start()
    }

    func applicationWillTerminate(_ notification: Notification) {
        // Flush any debounced config write — quitting inside the 0.4 s window would lose it.
        // A second copy that is quitting must not write: the running one owns the config.
        if isPrimary { AppController.shared.store.saveNow() }
    }
}
