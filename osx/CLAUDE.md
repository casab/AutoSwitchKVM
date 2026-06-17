# CLAUDE.md

Guidance for Claude when working in this repository.

## What this is

**AutoSwitch KVM** — a native macOS menu bar app (SwiftUI, macOS 13+) that automatically
connects/disconnects Bluetooth devices when a chosen USB "switcher source" appears or disappears.
Use case: a KVM switch exposes a USB hub to whichever computer is selected; when this Mac is
selected its trackpad/keyboard reconnect, and when it's switched away they release for the other
host. Replaces the Hammerspoon prototype in `reference/hammerspoon_init.lua`.

Design rationale lives in `PLAN.md`; the outstanding roadmap is in `IMPROVEMENTS.md`.

## Build & run

Two paths — both open the same sources:

- **Swift Package (fast dev loop):** open `Package.swift` in Xcode, select the `AutoSwitchKVM`
  scheme + "My Mac", Run.
- **Real `.app` (for distribution, notifications, login item):**
  `brew install xcodegen` once, then `xcodegen generate` → open `AutoSwitchKVM.xcodeproj`, Run.

The app is menu-bar-only (`NSApp.setActivationPolicy(.accessory)` + `LSUIElement`).

**Building requires macOS + Xcode 15+** (IOKit/IOBluetooth, SwiftUI `MenuBarExtra`, `SMAppService`).
Do not attempt to compile in a non-macOS sandbox — there's no SDK there. Write carefully, review,
and hand builds/tests to a macOS environment.

## Test

```sh
swift test          # or ⌘U in Xcode with Package.swift open
```

Tests live in `Tests/AutoSwitchKVMTests/` and use `@testable import AutoSwitchKVM`. The engine is
unit-testable because it depends on the `USBMonitoring` and `BluetoothController` **protocols**,
which `Mocks.swift` fakes. `SelectionEngine.evaluateNow()` / `handleUSB(_:)` are `internal` test
seams that drive the state machine deterministically (no debounce timers). `ConfigStore(directory:)`
takes an injectable directory so tests don't touch the real config.

CI (`.github/workflows/ci.yml`) runs `swift build` + `swift test` on every push/PR, plus an
advisory `swift-format` lint (`.swift-format` config). The lint is `continue-on-error` for now.

## Architecture

Single executable target, organized by folder under `Sources/AutoSwitchKVM/`:

- `App/` — `AutoSwitchKVMApp` (`@main`, `MenuBarExtra` + Settings `Window`), `AppController`
  (coordinator that owns all managers and exposes actions to the UI).
- `Models/` — `Models.swift` (`USBSource`, `BTDevice`, `Profile`, `AppConfig`), `ConfigStore` (JSON
  persistence). `AppConfig` holds `profiles` + `activeProfileID`; `source`/`devices` are **computed
  accessors onto the active profile** (so call sites stay simple). Custom `Codable` migrates legacy
  single-source configs into a "Default" profile. Global options (timing/notifications/pause/dock)
  are app-wide, not per-profile.
- `USB/` — `USBMonitoring` (protocol), `USBMonitor` (IOKit attach/detach + enumeration),
  `SourceLearner` (detects the source by watching USB changes during a KVM switch).
- `Bluetooth/` — `BluetoothController` (protocol + errors + `withTimeout`),
  `IOBluetoothController` (native impl: synchronous connect/disconnect, async pair, private-API
  unpair, adapter power via `isPoweredOn()`, and connect/disconnect event monitoring).
- `Engine/` — `SelectionEngine` (debounced state machine: source presence → connect/disconnect).
- `System/` — `SleepWakeMonitor`, `LoginItem` (`SMAppService`), `DockManager`, `Notifier`,
  `HotKeyManager` (Carbon `RegisterEventHotKey`; user-assignable combos via `KeyShortcut`, off by
  default; `apply(enabled:pause:connectAll:disconnectAll:)` is idempotent).
- `Support/` — `Log` (`os.Logger` wrappers: subsystem `com.enginal.AutoSwitchKVM`, categories
  `app`/`usb`/`bluetooth`/`engine`) which also mirror into `DebugLog` (in-app buffer shown +
  exportable from Settings ▸ Diagnostics ▸ Debug logs). Use `Log.*` instead of `NSLog`/`print`.
- `UI/` — `MenuContentView` (menu panel + profile picker), `SettingsView` (profiles bar +
  Source/Devices/General/Extras/Diagnostics tabs), `SourceConfiguration` (Learn + manual source
  editor sheets), `DiagnosticsView` (read-only USB + Bluetooth state).

Data flow: `USBMonitor` emits attach/detach events → `SelectionEngine` debounces (separate
arrival/departure timings), tracks presence of the active profile's source product IDs, and on
transitions connects/disconnects the enabled
`BTDevice`s via `BluetoothController` (pairing first / unpairing after when `managePairing`).
`ConfigStore` persists `AppConfig`. `AppController` wires it together, mirrors state for the menu
bar icon, and re-seeds the engine on profile switches. Switching profiles re-points monitoring at
the new profile but does not force-disconnect the previous profile's devices.

## Conventions

- Managers are `@MainActor final class … : ObservableObject`; IOBluetooth/IOKit must be used on the
  main thread.
- New external dependencies go behind a **protocol** (like `BluetoothController`/`USBMonitoring`)
  so they can be mocked. Add tests when you touch the engine.
- Async/await throughout; Bluetooth calls are wrapped in `withTimeout(_:)`; the engine uses a
  `runToken` to cancel stale in-flight work and a `busyDevices` set so the status monitor doesn't
  fight in-flight transitions.
- `-strict-concurrency=complete` is on (both targets) and builds clean. Keep it clean: Combine
  sinks that touch `@MainActor` state hop via `MainActor.assumeIsolated` (they deliver on main);
  cross-task closures must be `Sendable`; system-delegate conformances use `@preconcurrency`.
- SwiftUI: inject shared objects via `.environmentObject`. Avoid multiple `.sheet(isPresented:)`
  on one view — use a single `.sheet(item:)` driven by an enum (see `SourceTab`).

## Gotchas

- **Source model = one vendor + a set of product IDs + a user-given name** (`USBSource`). The
  source editor enforces a single vendor. Don't reintroduce a multi-vendor model.
- **`connect` uses the synchronous `IOBluetoothDevice.openConnection()` on purpose.** The async
  `openConnection(_:)` callback (`connectionComplete:status:`) proved unreliable on the Magic
  Trackpad — it sometimes never fired even when the ACL link came up, leaving a dead HID. The
  synchronous call blocks until the connection is truly established and returns a real result code.
  Don't switch it back to async. The engine treats only a clean (non-throwing) connect as success.
- **Native unpair uses the private `-[IOBluetoothDevice remove]` selector** (same as `blueutil`)
  via an IMP cast in `IOBluetoothController` — there is no public unpair API. Its return value is
  unreliable, so success is judged by re-checking `isPaired()`. Validated on a Magic Trackpad; if it
  ever breaks, the fallback is to bundle `blueutil` behind the `BluetoothController` protocol.
- **Notifications and launch-at-login require a real `.app` bundle.** `Notifier` no-ops unless
  `Bundle.main.bundleURL.pathExtension == "app"` because `UNUserNotificationCenter.current()`
  crashes in a bare SwiftPM executable.
- **`Package.swift` embeds `Info.plist` via a `-sectcreate` linker flag** with a path relative to
  the package root (portable to CI; resolves for both `swift build` and Xcode-opened packages).
- **The app icon lives in `Resources/Assets.xcassets`**, wired into the XcodeGen `.app` target
  only (kept out of `Sources/AutoSwitchKVM/` so SwiftPM doesn't choke on an unhandled resource).
  Bundle ID is `com.enginal.AutoSwitchKVM`; builds are self-signed (ad-hoc) by default.
- Both SwiftPM and ad-hoc-signed `.app` runs log benign `os_log` noise (CoreUI / App-Intents /
  Spotlight donation failures). `OS_ACTIVITY_MODE=disable` in the Run scheme quiets it (but also
  hides our own `os.Logger` lines); proper Developer ID signing + install reduces it. Left off by
  default so logs stay visible.

## When making changes

- Touching the engine, config, or models → update/extend the tests in `Tests/AutoSwitchKVMTests/`.
- Adding a config field → for backward-compatible decoding of existing saved configs, decode with
  `decodeIfPresent ?? default` (Swift's synthesized `Decodable` throws on missing keys). `BTDevice`
  and `AppConfig` already have custom `init(from:)` doing this; follow that pattern.
- Connect order = the profile's device-list order (reorderable in the Devices tab); `BTDevice`
  `connectDelayMs` staggers an individual device.
- After finishing a roadmap item, prune it from `IMPROVEMENTS.md`.
