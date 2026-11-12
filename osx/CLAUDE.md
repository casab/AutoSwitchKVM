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

**Release builds** (no Apple Developer ID yet, so no notarization):
- `tools/release.sh <version>` builds a universal Release `.app` from the XcodeGen project,
  stamps the version (`CFBundleShortVersionString` without a `-beta` suffix; `CFBundleVersion` =
  `BUILD_NUMBER`, default the commit count), signs it with `SIGN_IDENTITY` (default ad-hoc, `--deep`
  so the embedded Sparkle helpers match) and writes `build/release/AutoSwitchKVM-<version>-macos.dmg`
  + `.sha256`. Runs locally as is.
- `.github/workflows/macos-release.yml` runs it on the same `v*` tags as the Windows release,
  waits for the release the Windows workflow creates, and attaches the DMG (plus, for stable tags,
  the Sparkle `appcast.xml` from `tools/make-appcast.sh`). It appends macOS install notes to the
  release body once (`<!-- macos-notes -->` marker). The DMG is also a run artifact.
- Secrets: `MACOS_SIGNING_P12` + `MACOS_SIGNING_P12_PASSWORD` (optional self-made code-signing
  certificate: keeps the code identity fixed, so the Bluetooth permission and login item survive
  updates; ad-hoc builds get a new identity each time), `SPARKLE_ED_PRIVATE_KEY` (without it no
  appcast is published).
- **Updates (Sparkle 2):** linked only into the XcodeGen target (`project.yml` `packages`); the
  SwiftPM build used by tests/CI has no Sparkle, and `App/AppUpdater` compiles the updater
  `#if canImport(Sparkle)`. Feed: `SUFeedURL` = `…/releases/latest/download/appcast.xml` (GitHub's
  "latest" is the newest stable release, so pre-releases are never offered). Updates are verified
  with `SUPublicEDKey` in `project.yml`; while it's empty the updater stays off. Settings ▸ General
  ▸ Updates (version, automatic checks, Check for Updates…) and an Updates button in the panel.
- When the Apple Developer membership exists: Developer ID signing, hardened runtime, notarization
  and stapling go into `release.sh` + the workflow; nothing else changes.

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

- `App/` — `AutoSwitchKVMApp` (`@main`, `MenuBarExtra` + Settings `Window`; the delegate quits a
  second copy via `SingleInstance`), `AppController` (coordinator that owns all managers and
  exposes actions to the UI), `AppUpdater` (Sparkle wrapper; see Build & run).
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
  `softRetryIntervalMs`, `maxSoftRetries`, `releaseRetryDelayMs`, `postPairSettleMs`,
  `postPairHidWaitMs`.
- `System/` — `SingleInstance` (`flock` on `Application Support/AutoSwitchKVM/instance.lock`:
  two copies would fight over the same devices), `SleepWakeMonitor`, `LoginItem` (`SMAppService`),
  `DockManager`, `Notifier`,
  `HotKeyManager` (Carbon `RegisterEventHotKey`; user-assignable combos via `KeyShortcut`, off by
  default; `apply(enabled:pause:connectAll:disconnectAll:)` is idempotent).
- `Support/` — `Log` (`os.Logger` wrappers: subsystem `com.enginal.AutoSwitchKVM`, categories
  `app`/`usb`/`bluetooth`/`engine`; levels `error`/`warn`/`log` (info)/`debug`, plus `always`),
  gated by `LogThreshold` from `AppConfig.logLevel` (spec §10; Settings ▸ Diagnostics ▸ Log
  detail; applied live, the change itself is always logged). Classify like Windows: `error` =
  failed and needs attention; `warn` = recovered/worked around (hard try failures, fallbacks);
  `log` = the handoff story and user actions, ~one line per event; `debug` = step detail, raw
  Bluetooth calls (the whole `bluetooth` category except adapter state and real problems), waits,
  superseded runs, and expected refusals (`deviceBusy`/`operationInProgress` tries). One fact,
  one info line: `AppController.append(_:level:)` (the in-app activity list) passes `.debug`
  when the engine or a system monitor already logged the same event. Budget, guarded by
  `LogLevelTests`: a clean arrive + leave is 6 lines at info, 0 at warning. The log
  mirrors into `FileLog` (the persistent daily file, started by `AppController.init`, never by
  tests) and
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
- **Identical hub IDs exist on this desk: match the source by bus location** (`USBSource.locationIDs`).
  A dock permanently carries Genesys `0x0610` @ `0x00120000` and `0x0626` @ `0x00220000`. The KVM's
  *switching* hubs have the same IDs at `0x00124000` / `0x00224000` (keyboard + receiver hang off
  them). Matching by IDs alone made the Mac think it was selected at startup while the KVM was on
  Windows; switching only "worked" because presence was tracked per product ID. Presence is now
  tracked per source device, Learn/manual pick record locations, and the reconcile compares
  individual devices. Seed logs a warning when an unpinned source matches the same IDs at several
  locations. If the dock moves to another port, the locations change: re-learn the source.
- **`connect` is asynchronous: `openConnection(self)` plus a cooperative wait (since 2026-11-12).**
  It used to call the synchronous `openConnection()` on purpose, because its result was the only
  reliable success signal; the async `connectionComplete:status:` callback sometimes never fired on
  the Magic Trackpad. On hardware, though, the sync call held the main thread for 20 s on every
  failed try (10 times in one session, with HID already known to have failed after 1.5 s). That froze
  the UI and delayed USB switch-away detection, so this Mac released the trackpad late. Success is
  now the HID check (`isHIDReady`). The callback is only an early hint, the link state is the
  fallback when it never fires, and `connect` gives up after 12 s. `disconnect` cancels a pending
  async open. Don't go back to the synchronous call. Not yet validated on hardware: if connects
  regress, compare `openConnection … (async)` lines with `bluetoothd`'s `HID connection` results.
- **Native unpair uses the private `-[IOBluetoothDevice remove]` selector** (same as `blueutil`)
  via an IMP cast in `IOBluetoothController` — there is no public unpair API. Its return value is
  unreliable, so success is judged by re-checking `isPaired()`. Validated on a Magic Trackpad; if it
  ever breaks, the fallback is to bundle `blueutil` behind the `BluetoothController` protocol.
- **Nothing may block the main thread for long.** IOBluetooth runs on the main actor, so a blocking
  call stalls USB events, debounce timers, the poll and the UI. A switch-away is then only seen when
  the call returns (seen on hardware: the departure was logged at the exact millisecond a 20 s
  `openConnection` returned). `closeConnection` and `remove` return at once; `blockingCall` warns
  above 2 s.
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
- **A pair waits out a recent unpair of the same device** (`waitForUnpairToSettle`). A quick KVM
  bounce starts the connect run while the superseded release is still in `-remove` (seen at
  22:58:24, 2026-11-12), and bluetoothd deletes the old key ~1.5 s after the link drops. So the
  engine routes every unpair through `unpairTracked`, waits for one still in flight, and keeps
  `postUnpairSettleMs` (2 s) between an unpair returning and the next pair. With no connect delay,
  nothing else spaces them.
- **A seed with the source absent releases what this Mac holds** (spec §3 Evaluate step 5,
  `reconcileOnSeed` → `releaseHeld`). `seed()` clears `automationActed`, so the normal "source
  gone" transition can't fire after it. Launch while switched away, unpause after the KVM moved, or
  a profile switch left the trackpad bonded here before. Only connected or bonded devices are
  touched, and only a connected one gets a notice. A manual connect made while switched away is
  released by the next seed.
- **No connect delay is needed** (since 2026-11-12). The old "don't pair before the other host
  has released" rule (6–12 s delays) was masking the pairing-link bug below. With the link kept,
  the user ran 0 ms delay and 400/400 ms debounces on both apps, and handoffs worked. The defaults
  are now departure debounce 400 ms (was 1200) and `connectDelayMs` 0. The picker still offers up to
  12 s for diagnosis.
- **Leave the pairing link alone after a successful pair** (since 2026-11-12). `PairingHelper`
  no longer calls `IOBluetoothDevicePair.stop()` on success (it closes the link the pairer opened)
  and keeps the finished object alive for 30 s; the engine waits up to `postPairHidWaitMs` (3 s)
  for HID on that link and drops/reconnects only without it. Cutting the link mid-HID-setup
  (bluetoothd: HID result 2603) made bluetoothd re-page the trackpad within 150–450 ms; the
  trackpad opened HID on that link, the Mac refused it (`Policy enforcement failed STATUS 706`,
  `securityFailed 1`), and every later HID attempt failed 706 within ~15 ms, even after a fresh
  re-pair, until the trackpad bonded with Windows or was power-cycled. In the app log this shows
  as `openConnection … OK (~100 ms)` (it adopted that link) followed by "link up but no HID".
  Verified 2026-11-12: HID on the pairing link after ~265 ms, 6/6, and fine with a 1 s connect delay.
- **`bluetoothd` is the best evidence for a failed handoff.** `/usr/bin/log show --predicate
  'process == "bluetoothd"'` (plain `log` is a zsh builtin) shows ACL up/down (with `incoming`),
  pairing results, `HID connection … status`, and `Policy enforcement failed STATUS 706` for each
  app call; see agent-notes/2026-11-12-windows-first-joint-run.md.
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
