// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "AutoSwitchKVM",
    platforms: [
        .macOS(.v13)
    ],
    targets: [
        .executableTarget(
            name: "AutoSwitchKVM",
            path: "Sources/AutoSwitchKVM",
            swiftSettings: [
                .unsafeFlags(["-strict-concurrency=complete"])
            ],
            linkerSettings: [
                .linkedFramework("IOKit"),
                .linkedFramework("IOBluetooth"),
                .linkedFramework("AppKit"),
                .linkedFramework("UserNotifications"),
                .linkedFramework("Carbon"),
                // Embed an Info.plist into the executable so the app has a bundle identifier,
                // LSUIElement (menu-bar only), and Bluetooth usage strings even when run as a
                // SwiftPM executable. Path is relative to the package root, which is the linker's
                // working directory for both `swift build` and Xcode-opened packages (portable to CI).
                .unsafeFlags([
                    "-Xlinker", "-sectcreate",
                    "-Xlinker", "__TEXT",
                    "-Xlinker", "__info_plist",
                    "-Xlinker", "Info.plist"
                ])
            ]
        ),
        .testTarget(
            name: "AutoSwitchKVMTests",
            dependencies: ["AutoSwitchKVM"],
            path: "Tests/AutoSwitchKVMTests",
            swiftSettings: [
                .unsafeFlags(["-strict-concurrency=complete"])
            ],
            // @testable import links the executable's objects, which reference these frameworks.
            linkerSettings: [
                .linkedFramework("IOKit"),
                .linkedFramework("IOBluetooth"),
                .linkedFramework("AppKit"),
                .linkedFramework("UserNotifications"),
                .linkedFramework("Carbon")
            ]
        )
    ]
)
