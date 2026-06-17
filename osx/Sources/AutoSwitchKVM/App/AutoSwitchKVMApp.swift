import SwiftUI
import AppKit

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
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        AppController.shared.start()
    }
}
