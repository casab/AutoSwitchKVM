# Switch Status Panel (macOS) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A small floating panel at the top-right that shows each device switching live when the KVM moves, then hides by itself, replacing device notifications on macOS (mirrors Windows' `StatusPopup`).

**Architecture:** Pure, testable `SwitchStatusModel` decides which rows exist, what they say and when to hide. A thin AppKit `SwitchStatusPanel` (non-activating `NSPanel` hosting a SwiftUI view) feeds it from the engine's `@Published` state and shows/hides it. `AppController` wires it up in place of the `UNUserNotification` device notices.

**Tech Stack:** Swift 6 / SwiftUI / AppKit, XCTest, `swift build` / `swift test`, `xcrun swift-format`.

**Spec:** `osx/docs/superpowers/specs/2026-11-16-switch-status-panel-design.md`

## Global Constraints

- Never takes focus: the panel never becomes key or main (`.nonactivatingPanel`, `canBecomeKey == false`).
- Hold times: 3.5 s after the last change once nothing is connecting; 8 s if any row shows a problem; never hides while hovered.
- Setting key stays `showNotifications` (shared with Windows; golden fixture unchanged). Settings label "Switch status", description "While devices switch, show their status in a small panel under the menu bar. It hides by itself and makes no sound."
- `notifyUnexpectedDisconnect` shows a drop in the panel even when `showNotifications` is off.
- No device `UNUserNotification`s after this change.
- Asleep wording: "Asleep: wake it to connect" (calm, not a problem).
- Lint: `xcrun swift-format lint --recursive --strict Sources Tests` clean; `swift test` green.

## Review Focus

- App launch with devices already connected: no panel (launch is Idle/nil → resting). Test in Task 2.
- KVM bounces (leaves and comes back within the hold): the new burst clears old rows and shows the new header. Test in Task 2.
- A device removed from config while its row is shown: its row disappears. Test in Task 2.
- Pointer resting on the panel across a whole switch: it stays and updates live, hides 3.5 s after the pointer leaves. Test in Task 2 (hover arg).
- Panel off (`showNotifications` false) and no drop: nothing is shown, ever. Test in Task 2.

---

### Task 1: Engine exposes which devices are asleep

**Files:**
- Modify: `osx/Sources/AutoSwitchKVM/Engine/SelectionEngine.swift` (`slowDevices` declaration)
- Test: `osx/Tests/AutoSwitchKVMTests/ChannelSwitchTests.swift`

**Interfaces:**
- Produces: `@Published private(set) var slowDevices: Set<UUID>` on `SelectionEngine` (devices in slow mode; cleared when they connect).

- [ ] **Step 1: Write the failing test** `testASleepingDeviceIsReportedAsleepUntilItWakes` (in the "doesn't answer" section; reuse `makeSleepingK380()` and the wake hook from `testASleepingDeviceThatWakesUpIsConnectedAtOnce`): record `f.engine.slowDevices.contains(f.device.id)` inside the sleep hook just before waking it; assert it was `true`, and after `await arrive(f)` assert `f.engine.slowDevices.isEmpty`.
- [ ] **Step 2: Run it:** `cd osx && swift test --filter ChannelSwitchTests/testASleepingDeviceIsReportedAsleepUntilItWakes`. Expected: compile error (`slowDevices` is private).
- [ ] **Step 3: Implement:** change `private var slowDevices = Set<UUID>()` to `@Published private(set) var slowDevices = Set<UUID>()`; doc comment "Devices in slow mode (asleep or off): the status panel shows them as asleep."
- [ ] **Step 4: Run it again.** Expected: PASS. Then `swift test`: all pass.
- [ ] **Step 5: Commit** `macOS engine: publish slow-mode (asleep) devices`.

### Task 2: `SwitchStatusModel` (rows, header, hide timing)

**Files:**
- Create: `osx/Sources/AutoSwitchKVM/UI/SwitchStatusModel.swift`
- Test: `osx/Tests/AutoSwitchKVMTests/SwitchStatusModelTests.swift`

**Interfaces:**
- Consumes: `DeviceStatus`, `BTDevice`, `MenuContentView.symbol(for:)` (device symbol by name).
- Produces:
  ```swift
  struct SwitchStatusRow: Identifiable, Equatable {
      enum Tone { case busy, ok, calm, problem }
      let id: UUID; var name: String; var symbol: String; var text: String; var tone: Tone
  }
  @MainActor final class SwitchStatusModel: ObservableObject {
      @Published private(set) var rows: [SwitchStatusRow]
      @Published private(set) var header: String          // "KVM → this Mac" / "KVM → the other computer"
      @Published private(set) var visible: Bool
      var showSwitches: Bool                               // showNotifications
      var showDrops: Bool                                  // notifyUnexpectedDisconnect
      func update(devices: [BTDevice], statuses: [UUID: DeviceStatus], selected: Bool,
                  asleep: Set<UUID>, switchesChannels: (BTDevice) -> Bool, now: Date)
      func noteDrop(_ device: BTDevice, now: Date)          // unexpected disconnect
      func tick(now: Date, hovering: Bool)                 // hides when due
      func dismiss()                                       // click / explicit hide
  }
  ```
- Row text by status: `.connecting` → "Connecting…" (busy); `.connected` → "Connected" (ok); `.disconnected` → asleep ? "Asleep: wake it to connect" (calm) : managePairing ? "Released" : switchesChannels ? "Sent to the other computer" : "Disconnected" (ok); `.error(m)` → `m` (problem); `.bluetoothOff` → "Bluetooth is off" (problem). Drop row: "Dropped unexpectedly" (problem).
- Counting rule: a device's row is added/updated when its status *kind* changes, except into `.idle` and except from `nil`/`.idle` directly to `.connected`/`.disconnected` (launch). The first `update` call only records statuses. A change of `selected` starts a new burst: rows cleared, header set. Rows of devices no longer in `devices` are removed.
- Hiding: `visible` becomes true when a row is added/updated and `showSwitches` (or the row is a drop and `showDrops`). `tick` hides when not hovering and `now - lastChange >= 3.5 s` (8 s if any row is `.problem`) and no row is `.busy`; hiding clears rows.

- [ ] **Step 1: Write the failing tests** in `SwitchStatusModelTests` (devices `trackpad` managePairing, `k380`, `speaker`; `t0 = Date(timeIntervalSince1970: 0)`):
  - `testLaunchShowsNothing`: first `update` with all `.connected` → `visible == false`, `rows.isEmpty`; a second identical update → still nothing.
  - `testASwitchShowsEachChangingDeviceLive`: from all `.disconnected`, `selected: true` with trackpad `.connecting` → `visible`, `header == "KVM → this Mac"`, one row "Connecting…" `.busy`; then trackpad `.connected` → row text "Connected", tone `.ok`.
  - `testReleaseTexts`: `selected: false`, trackpad/k380/speaker → `.connecting` then `.disconnected` with `switchesChannels` true only for k380 → texts "Released", "Sent to the other computer", "Disconnected"; header "KVM → the other computer".
  - `testAsleepIsCalm`: k380 `.connecting` → `.disconnected` with `asleep: [k380.id]` → text "Asleep: wake it to connect", tone `.calm`.
  - `testHidesAfterHoldTimes`: after the last change at `t0`, nothing busy: `tick(t0+3.4)` visible; `tick(t0+3.5)` hidden and rows empty. With an `.error("gave up")` row: visible at `t0+7.9`, hidden at `t0+8.0`. With a `.busy` row: still visible at `t0+60`.
  - `testHoverKeepsItOpen`: `tick(t0+10, hovering: true)` visible; `tick(t0+10, hovering: false)` hidden.
  - `testANewBurstClearsOldRows`: rows from `selected: true`, then `selected: false` with one device changing → only that device's row, header "KVM → the other computer".
  - `testRemovedDeviceLosesItsRow`: a row for speaker, then `update` without speaker in `devices` → no speaker row.
  - `testOffShowsNothingButDropsWhenAsked`: `showSwitches = false, showDrops = false` → a switch shows nothing; `noteDrop` shows nothing. `showDrops = true` → `noteDrop(trackpad)` → visible, one row "Dropped unexpectedly", `.problem`.
- [ ] **Step 2: Run:** `cd osx && swift test --filter SwitchStatusModelTests`. Expected: compile failure (no `SwitchStatusModel`).
- [ ] **Step 3: Implement `SwitchStatusModel`** in `UI/SwitchStatusModel.swift` per the interface and rules above (keep `lastKinds: [UUID: kind]`, `lastChange: Date`, `lastSelected: Bool?`, `primed: Bool`). Symbol: `MenuContentView.symbol(for: device.name)`.
- [ ] **Step 4: Run** the filter, then `swift test`. Expected: all pass. Lint clean.
- [ ] **Step 5: Commit** `macOS: SwitchStatusModel (rows, header, hide timing) with tests`.

### Task 3: The panel, wiring, settings, docs

**Files:**
- Create: `osx/Sources/AutoSwitchKVM/UI/SwitchStatusPanel.swift`
- Modify: `osx/Sources/AutoSwitchKVM/App/AppController.swift` (notices → panel), `osx/Sources/AutoSwitchKVM/UI/SettingsView.swift` (toggle labels), `osx/Sources/AutoSwitchKVM/App/AutoSwitchKVMApp.swift` (demo launch argument), `osx/CLAUDE.md`, `osx/README.md` (notifications wording, if present), `CHANGELOG.md` (macOS line).

**Interfaces:**
- Consumes: `SwitchStatusModel` (Task 2), `SelectionEngine.statuses/selected/slowDevices` (`@Published`), `SelectionEngine.switchesChannels(_:)`, `ConfigStore.config`.
- Produces: `@MainActor final class SwitchStatusPanel { init(model: SwitchStatusModel, openSettings: @escaping () -> Void); func start() }`.

- [ ] **Step 1: Implement `SwitchStatusPanel`:** a `NSPanel` subclass with `canBecomeKey/canBecomeMain` false, style `[.nonactivatingPanel, .borderless]`, `level = .statusBar`, `collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]`, `hidesOnDeactivate = false`, transparent background, hosting a SwiftUI view (`NSHostingView`): 320 pt wide, `.regularMaterial` rounded 12 pt, header line, then rows (symbol, name, text; tone colors: busy secondary + `ProgressView().controlSize(.small)`, ok green check, calm secondary moon, problem orange triangle). Placed at the top-right of the screen with the menu bar: `x = visibleFrame.maxX - width - 8`, `y = visibleFrame.maxY - height - 8`; resize to content height on row changes. `orderFrontRegardless()` to show (never `makeKey`), fade 0.15 s. Hover via `.onHover` into the model's tick; click → `openSettings()` + `model.dismiss()`. A 250 ms `Timer` calls `model.tick(now:hovering:)`. Not shown while a visible window whose class name contains "MenuBarExtra" is up (the menu panel shows the same).
- [ ] **Step 2: Wire in `AppController.start()`:** create the model and panel; Combine `engine.$statuses`, `engine.$selected`, `engine.$slowDevices`, `store.$config` → `model.update(...)` (on main); `showSwitches/showDrops` from config. `engine.onNotice` no longer posts notifications (keep the hook nil or log-only). `onUnexpectedDisconnect` → `model.noteDrop(device, now:)` when `notifyUnexpectedDisconnect`. Remove `notifier.requestAuthorizationIfNeeded()` for device events (and the import-time request at the config import path). `openSettings`: activate the app and open the "settings" window (same as the menu's Settings… action).
- [ ] **Step 3: Settings labels:** "Show notifications" → "Switch status" with the description from Global Constraints; "Notify when a device drops unexpectedly" → "Show unexpected disconnects too".
- [ ] **Step 4: Demo argument:** launching with `--status-panel-demo` shows the panel with four demo rows (Connecting…, Connected, Asleep, a problem) for 10 s, without touching devices.
- [ ] **Step 5: Verify:** `swift build`, `swift test`, lint clean. Dev build (`tools/release.sh` with the dev identity), run `open build/xcode/Release/AutoSwitchKVM.app --args --status-panel-demo`: panel at top-right, frontmost app keeps focus (typing in another app continues), hover holds it, click opens Settings. Then a real KVM switch each way with "Switch status" on.
- [ ] **Step 6: Docs:** `osx/CLAUDE.md` (a "Switch status panel" bullet: files, never-key panel, hold times, demo argument); `CHANGELOG.md` Unreleased macOS: "Device notifications are now a small status panel under the menu bar: it shows each device connecting, connected or released while the KVM switches, then hides by itself. No sound, nothing in Notification Center (Settings ▸ Switch status)."; README wording if it mentions notifications.
- [ ] **Step 7: Commit** `macOS: switch status panel under the menu bar instead of device notifications`.
