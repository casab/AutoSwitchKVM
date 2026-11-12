# CLAUDE.md

Guidance for Claude when working in this repository.

## What this is

**AutoSwitch KVM** — a native macOS menu bar app (SwiftUI, macOS 13+) that automatically
connects/disconnects Bluetooth devices when a chosen USB "switcher source" appears or disappears.
Use case: a KVM switch exposes a USB hub to whichever computer is selected; when this Mac is
selected its trackpad/keyboard reconnect, and when it's switched away they release for the other
host. Replaces the Hammerspoon prototype in `reference/hammerspoon_init.lua`.

**Logs to read when debugging a handoff:** `~/Library/Logs/AutoSwitchKVM/app-yyyyMMdd.log` (14 days
kept; Settings ▸ Diagnostics ▸ Show Log Files). Config: `~/Library/Application Support/AutoSwitchKVM/
config.json`. Categories: `engine` (runs, tries, steps with timings, retries, aborts, give-ups,
handoff timeline), `bluetooth` (raw IOBluetooth calls with decoded result codes and durations),
`usb` (source ids attaching/detaching), `app` (startup diagnostics, sleep/wake, config I/O). Don't
rely on `log show`: a real run on 2026-11-11 left none of the app's own lines in the unified log.

Design rationale lives in `PLAN.md`; the outstanding roadmap is in `IMPROVEMENTS.md`. This folder
is the macOS half of a monorepo: the platform-neutral behavior contract is `../SPECIFICATION.md`,
and the Windows port lives in `../windows/`.

## Coordinating with the Windows agent

Another agent usually works on `windows/` in parallel, on a Windows PC. Use `agent-notes/` at the
repo root (protocol: `../agent-notes/README.md`):

- At session start, `git pull --ff-only` and check open notes `to: osx|all`:
  `grep -lE '^status: (open|acknowledged)' agent-notes/2*.md | xargs grep -lE '^to: (osx|all)'`
  (run from the repo root). Read them before related work; reply by appending a section and
  updating `status:`.
- Write a note `to: windows` when you change `SPECIFICATION.md`/config keys, see trackpad or
  Windows-host behavior from the Mac side (e.g. how long Windows takes to release), or fix a bug
  whose pattern likely exists in `windows/`. Tell the user when a note is waiting to be
  committed/pushed.
- `SPECIFICATION.md` sections marked "macOS pending" are behavior the Windows engine already
  has and this engine should get. Don't edit `windows/` yourself unless the user asks.

## Build & run

Two paths — both open the same sources:

- **Swift Package (fast dev loop):** open `Package.swift` in Xcode, select the `AutoSwitchKVM`
  scheme + "My Mac", Run.
- **Real `.app` (for distribution, notifications, login item):**
  `brew install xcodegen` once, then `xcodegen generate` → open `AutoSwitchKVM.xcodeproj`, Run.

The app is menu-bar-only (`NSApp.setActivationPolicy(.accessory)` + `LSUIElement`).

**Building requires macOS + Xcode 16+** (Swift 6 toolchain — the sources use Swift 6 compiler
features like `@preconcurrency` conformances (SE-0423), while staying in the Swift 5 language
mode; plus IOKit/IOBluetooth, SwiftUI `MenuBarExtra`, `SMAppService`).
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

CI (`.github/workflows/ci.yml`) runs `swift build` + `swift test` on pushes to `main` and on
every PR (pinned Xcode 16.x), plus a blocking `swift-format` lint using the Xcode toolchain's
swift-format (`.swift-format` config).

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
- `Bluetooth/` — `BluetoothController` (protocol + `withTimeout`), `BluetoothErrors` (`BTError`,
  `BTFailureKind`, and `BTCode`, which names IOReturn/HCI result codes and maps them to failure
  kinds), `IOBluetoothController` (native impl: synchronous connect/disconnect, async pair that
  `unpair` can cancel, private-API unpair, adapter power via `isPoweredOn()`, and
  connect/disconnect event monitoring; logs every raw call with its duration).
- `Engine/` — `SelectionEngine` (debounced state machine: source presence → connect/disconnect).
  Work runs in *runs* (token + trigger); every await re-checks `staleReason`. Failure
  classification per SPECIFICATION.md §3 (soft retries for busy/in-progress; stale-bond re-pair
  only after timeout/auth/unknown). Per-device manual entry points (`connectDeviceNow`,
  `disconnectDeviceNow`, `pairDeviceNow`, `unpairDeviceNow`) and sleep handling
  (`prepareForSleep()` / `resumeAfterWake()`). Test seams: `sleepHook` (instant delays),
  `softRetryIntervalMs`, `maxSoftRetries`, `releaseRetryDelayMs`, `postPairSettleMs`.
- `System/` — `SleepWakeMonitor`, `LoginItem` (`SMAppService`), `DockManager`, `Notifier`,
  `HotKeyManager` (Carbon `RegisterEventHotKey`; user-assignable combos via `KeyShortcut`, off by
  default; `apply(enabled:pause:connectAll:disconnectAll:)` is idempotent).
- `Support/` — `Log` (`os.Logger` wrappers: subsystem `com.enginal.AutoSwitchKVM`, categories
  `app`/`usb`/`bluetooth`/`engine`; levels `log`/`warn`/`error`/`debug`) which also mirror into
  `FileLog` (the persistent daily file, started by `AppController.init`, never by tests) and
  `DebugLog` (in-app buffer shown + exportable from Settings ▸ Diagnostics ▸ Debug logs).
  `Elapsed` formats durations for log lines. Use `Log.*` instead of `NSLog`/`print`.
- `System/BluetoothPermission` — reads the app's Bluetooth privacy permission (logged at launch;
  a denied permission makes IOBluetooth fail quietly, and ad-hoc rebuilds can reset it).
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
  `runToken` to cancel stale in-flight work and ref-counted busy devices so the status monitor
  doesn't fight in-flight transitions.
- **Never call `bt` directly from the app layer.** Manual actions and the sleep release go through
  the engine (busy marking, explicit status, run supersession). Calling the controller directly let
  the stack's late "disconnected" event trigger an auto-reconnect that undid the user's action.
- **No silent `try?` on Bluetooth, config, or file paths.** A step that may fail without aborting
  the flow goes through the engine's `bestEffort` (logs a warning); everything else logs the error
  with context. Log decisions in `engine`, raw calls in `bluetooth`.
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
- **The synchronous IOBluetooth calls block the main thread.** `openConnection()` took 20 s in a
  July hardware log when the device didn't answer. Meanwhile USB events, debounce timers, the
  status poll, and the UI all stall, so a switch-away during a blocked connect is only seen after
  it returns. `withTimeout` cannot interrupt it either; the timeout is only observed when the call
  returns. `IOBluetoothController` logs a warning whenever a call holds the main thread for over 2 s.
  Moving these calls off the main thread is an open item in `IMPROVEMENTS.md`; it needs a hardware
  run to validate, since IOBluetooth is documented as main-thread.
- **Failure classification uses IOBluetooth result codes** (`BTCode` in `BluetoothErrors.swift`).
  IOBluetooth returns IOKit `IOReturn` values (`0xE00002xx`) or raw HCI error codes (small numbers,
  e.g. 4 = page timeout). On this Mac, the stale-bond case appears as `kIOReturnTimeout` from
  `openConnection`, so `timeout` stays re-pair-eligible. Refusals (HCI 0x0D–0x0F, 0x18) are soft
  and must not unpair.
- **"Connected" must mean HID is up, not just a link** (`isHIDReady`). On hardware, a 1 ms
  `openConnection` on a bare link reported success while HID never started; the link idled out
  15 s later. bluetoothd publishes a working trackpad as a *registered* `IOHIDDevice`
  (`IOHIDUserDevice`, `Transport = "Bluetooth"`, `SerialNumber = "3C:50:02:BF:22:45"`). Match it
  with `IOServiceGetMatchingServices`, never a raw registry walk: failed attempts leave
  *unregistered* `IOHIDUserDevice` leftovers with the same serial. "Link up, no HID" is
  `hidNotReady`; 2 in one run stop early with a power-cycle hint, because the trackpad was wedged
  and retries only churned it. The positive case of this check still needs hardware confirmation.
  If the trackpad never counts as connected again, read the log's "registered Bluetooth HID
  serials" line.
- **`-[IOBluetoothDevice remove]` is deferred by bluetoothd.** It returns at once, the link drops
  ~1 s later, and the key is deleted ~1.5 s after that. `unpair` waits for the link to drop. The
  engine settles `postUnpairSettleMs` (2 s) before re-pairing, because a pair started straight
  after an unpair failed with HCI 0x02.
- **Don't pair before the other host has finished releasing** (~8 s after a switch on Windows
  today). The trackpad accepted a Mac pairing mid-release, never started HID, and needed a power
  cycle. Until the LAN release-ack exists, the trackpad's `connectDelayMs` is 6 s (picker now
  offers 4/6/8 s).
- **`bluetoothd` is the best evidence for a failed handoff.** `log show --predicate
  'process == "bluetoothd"'` shows ACL up/down (with `incoming`), pairing results, and
  `HID connection … status` for each app call; see agent-notes/2026-11-12-windows-first-joint-run.md.
- **A pair can land after a release.** If the source leaves while a stale run is mid-ceremony,
  the engine undoes a pair/connection that completes after the release, and `unpair` cancels a
  ceremony still in flight for that device. Without this the Mac holds the bond while switched away.
- **A bond record can go stale** (observed on hardware): the other host's late release — or a
  device power-cycle — can invalidate the link key while `isPaired()` still reads true, producing
  endless "pair: skipped" + `openConnection` timeouts. The engine self-heals: after a hard failure
  that may mean a stale bond (timeout / authentication / unknown), the next try for a
  `managePairing` device unpairs and re-pairs from scratch. An unexpected drop while the source is
  present triggers one rate-limited auto-reconnect (`autoReconnectOnDrop`, default on). Don't
  reintroduce a bare pair-if-not-paired retry loop, and don't make the re-pair unconditional again:
  after a refusal, it only destroys a good fresh bond.
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
- Engine behavior changes must stay in sync with `../SPECIFICATION.md` §3 and the Windows engine
  (`../windows/src/AutoSwitchKVM.Core/SelectionEngine.cs`). Update the spec and leave a note in
  `../agent-notes/` so the other side can mirror it.
- Adding a config field → for backward-compatible decoding of existing saved configs, decode with
  `decodeIfPresent ?? default` (Swift's synthesized `Decodable` throws on missing keys). `BTDevice`
  and `AppConfig` already have custom `init(from:)` doing this; follow that pattern.
- Connect order = the profile's device-list order (reorderable in the Devices tab); `BTDevice`
  `connectDelayMs` staggers an individual device.
- After finishing a roadmap item, prune it from `IMPROVEMENTS.md`.
