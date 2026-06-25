# AutoSwitch KVM — macOS

The macOS app. This is a platform folder — run all commands (`swift build`, `swift test`,
`xcodegen generate`) from here (`osx/`). Cross-platform overview is in the repo-root `README.md`.

A native macOS menu bar app that automatically connects/disconnects Bluetooth devices when a
chosen USB "switcher source" appears or disappears — e.g. when your KVM switches this Mac in,
its trackpad/keyboard reconnect; when it switches away, they release for the other host.

Replaces the Hammerspoon prototype in `reference/hammerspoon_init.lua`. See `PLAN.md` for the
full design.

## Requirements

- macOS 13 (Ventura) or later
- Xcode 15+ (for the SwiftUI / `MenuBarExtra` and `SMAppService` APIs)

## Open & run

There are two ways to build it. The Swift Package is fastest; the generated `.xcodeproj`
produces a proper `.app` bundle (recommended — it eliminates console noise from missing
bundle identity and is the path to signing).

### Option A — proper .app via XcodeGen (recommended)

```sh
brew install xcodegen            # once
cd <this folder>
xcodegen generate                # creates AutoSwitchKVM.xcodeproj
open AutoSwitchKVM.xcodeproj
```

Select the **AutoSwitchKVM** scheme + "My Mac" and Run.

The generated project is **self-signed** (ad-hoc, "Sign to Run Locally") out of the box — no
Apple account needed. To sign with your Apple Developer account instead (so Bluetooth/TCC
permissions and notification authorization persist across rebuilds), set your Team ID in
`project.yml` (`DEVELOPMENT_TEAM`) and change `CODE_SIGN_IDENTITY` to `Apple Development`, then
re-run `xcodegen generate`. Developer ID signing + notarization (for sharing the app) is a later
step — see `IMPROVEMENTS.md`.

### Option B — Swift Package (quick)

1. Open `Package.swift` in Xcode.
2. Select the **AutoSwitchKVM** scheme and a "My Mac" destination, then Run.

On first run, macOS may prompt for Bluetooth access — allow it. The app runs **unsandboxed**
(IOKit + IOBluetooth need it). Developer ID signing + notarization for distribution is a later
step — see `IMPROVEMENTS.md`.

### Quieting Xcode console noise

Running an unsigned dev build logs a lot of benign `os_log` chatter (CoreUI bundle lookups,
App Intents / Spotlight "donation" failures, Dock/status-item scene messages). None affect
behavior. To silence it when you don't need logs, add `OS_ACTIVITY_MODE = disable` to the Run
scheme (Product ▸ Scheme ▸ Edit Scheme ▸ Run ▸ Arguments ▸ Environment Variables). Note this also
hides the app's own `os.Logger` lines from the Xcode console (they remain in the in-app Activity
panel and Console.app / `log stream`), so leave it off while debugging.

## Using it

1. Click the menu bar icon ▸ **Settings…**
2. **Source** tab: either
   - **Learn source…** — click it, then switch your KVM to another computer and back. The app
     detects which USB devices appeared/disappeared, lets you **uncheck** anything that isn't part
     of the switch (e.g. a mouse), and **name** the source; or
   - **Pick manually…** — choose the device(s) from the attached list and name them.

   A source is one vendor plus its product IDs (a hub often exposes several), so all selected
   devices must share a vendor.
3. **Devices** tab: **Add** a Bluetooth device (pick from paired devices, or type a name +
   address like `3c-50-02-bf-22-45`). Toggle **Manage pairing** for devices like the Magic
   Trackpad that must be unpaired on release and re-paired on return. Reorder cards with the
   up/down arrows (devices connect top-to-bottom) and set a per-device **Connect delay** to stagger.
4. The app now connects enabled devices when the source appears and disconnects them when it's gone.

From the **menu bar** you can also **Pause/Resume automation** (the icon changes while paused) and
trigger **Connect all now** / **Disconnect all now** manually. In General you can enable
**Show notifications** (connect/disconnect alerts) and **Notify when a device drops unexpectedly**,
tune timing (separate arrival/departure debounce, retries, per-call timeout), and **Export…/Import…**
all settings as a JSON file.

**Profiles.** A profile is a named source + device set (e.g. "Desk", "Travel"). Switch between
them from the profile bar at the top of Settings, or from the menu bar — segmented buttons for up
to three profiles, an overflow (…) menu for more, hidden entirely when there's only one.
Add/rename/delete in Settings. Timing and other global options are shared across profiles.

**Global shortcuts.** Off by default. Enable in Settings ▸ General for system-wide hotkeys to
pause/resume, connect all, and disconnect all. Each combo is **user-assignable** — click Record and
press a combo with at least one of ⌃⌥⌘⇧ (defaults are ⌃⌥⌘P / ⌃⌥⌘C / ⌃⌥⌘D; ✕ clears one,
**Restore defaults** resets all three). No Accessibility permission needed.

## Manual device controls

Each device card (Devices tab) has manual **Connect / Disconnect / Pair / Unpair** buttons, with
results shown in the **Activity** log — handy for troubleshooting.

The riskiest piece, **unpair**, has no public API: we use the private `-[IOBluetoothDevice remove]`
selector (the same call `blueutil` makes), validated working on a Magic Trackpad. If it ever breaks
across a macOS release, the fallback is to bundle `blueutil` invoked only for unpair, swapped in
behind the `BluetoothController` protocol — no other code changes.

## Project layout

```
Package.swift                  SwiftPM manifest (app + test targets)
project.yml                    XcodeGen spec for a proper .app bundle
Info.plist                     Bundle identity / usage strings
Resources/Assets.xcassets      App icon (XcodeGen .app build only; not in the SwiftPM target)
Sources/AutoSwitchKVM/
  App/
    AutoSwitchKVMApp.swift     @main App: MenuBarExtra + Settings window, accessory policy
    AppController.swift        Coordinator: owns managers, wires them, exposes actions
  Models/
    Models.swift               USBSource, BTDevice, Profile, AppConfig (profiles + active accessors)
    ConfigStore.swift          JSON persistence (injectable directory for tests)
  USB/
    USBMonitoring.swift        Protocol the engine depends on (mockable)
    USBMonitor.swift           IOKit attach/detach notifications + enumeration
    SourceLearner.swift        Detects source devices by watching changes during a KVM switch
  Bluetooth/
    BluetoothController.swift  Backend protocol + errors + timeout helper
    IOBluetoothController.swift Native impl (sync connect, pair, private-API unpair, power, events)
  Engine/
    SelectionEngine.swift      Debounced state machine: source presence -> connect/disconnect
  System/
    SleepWakeMonitor.swift     Sleep -> disconnect; wake -> re-seed
    LoginItem.swift            Launch at login (SMAppService)
    DockManager.swift          Extras: auto-hide Dock on built-in-display-only
  UI/
    MenuContentView.swift      Menu bar panel (status, device cards, quick actions)
    SettingsView.swift         Profiles bar + Source / Devices / General / Extras / Diagnostics tabs
    SourceConfiguration.swift  Learn + manual source editor sheets (name + device checkboxes)
    DiagnosticsView.swift      Read-only USB + Bluetooth state for troubleshooting
Tests/AutoSwitchKVMTests/
  Mocks.swift                  Mock USB + Bluetooth for deterministic tests
  SelectionEngineTests.swift   Connect/disconnect transitions, pair ordering, pause, edge cases
  ConfigStoreTests.swift       Persistence round-trip
  ModelsTests.swift            Address normalization, display formatting
  ProfilesTests.swift          Legacy migration, active-profile accessors, profile round-trip
reference/hammerspoon_init.lua The original prototype this replaces
PLAN.md / IMPROVEMENTS.md      Design, milestones, roadmap
```

## Running tests

The engine, config persistence, and models are unit-tested via mocks (no hardware needed):

```sh
swift test
```

Or in Xcode (with `Package.swift` open): press ⌘U.

`swift build` + `swift test` also run in CI on every push/PR via GitHub Actions
(`.github/workflows/ci.yml`), alongside a blocking `swift-format` lint (config in `.swift-format`).

## Logs

The app logs via `os.Logger` under subsystem `com.enginal.AutoSwitchKVM` (categories: `app`,
`usb`, `bluetooth`, `engine`). Watch them live with:

```sh
log stream --predicate 'subsystem == "com.enginal.AutoSwitchKVM"'
```

or browse in Console.app. The same lines are captured in-app under **Settings ▸ Diagnostics ▸ Debug
logs**, where you can copy or export them to a file for sharing.

## Known caveats / things to verify on-device

- **Private unpair API** (`-[IOBluetoothDevice remove]`) — success is judged by re-checking
  `isPaired()` since the selector's return value is unreliable. Not guaranteed across macOS releases.
- **Connect uses the synchronous `openConnection()` deliberately** — the async callback proved
  unreliable for this handoff (link up but HID dead). Don't switch it back.
- **Programmatic pairing** of HID devices auto-confirms secure-simple-pairing prompts; a device
  that demands a PIN won't pair headlessly.
- **Launch at login** (`SMAppService`) needs a real app bundle; works when run from Xcode, not
  from a bare `swift run`.
- **Notifications** require a real `.app` bundle (the XcodeGen build). In a plain SwiftPM run they
  are silently skipped to avoid a `UNUserNotificationCenter` crash.
- **Menu bar icon** updates on source active/inactive; per-device status shows in the menu and
  the Devices tab.

## Next steps

See `IMPROVEMENTS.md` for the roadmap. Remaining: Developer ID signing + notarization for
distribution, lower-value polish, and the optional `blueutil` fallback. (The robustness and
engineering-quality passes — power awareness, reliable connect, backoff, per-event debounce,
disconnect notice, schema versioning, error banner, and blocking lint — are done.)
