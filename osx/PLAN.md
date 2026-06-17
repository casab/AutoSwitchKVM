# AutoSwitch KVM — Implementation Plan

A native macOS menu bar app (Swift / SwiftUI) that automatically connects/disconnects
Bluetooth devices when a chosen USB "switcher source" appears or disappears. Replaces the
existing Hammerspoon prototype (`reference/hammerspoon_init.lua`).

## 1. Goals

- Pick a **USB switcher source** from a dropdown of currently-attached USB devices.
- Manage **multiple Bluetooth devices**, each with an enable/disable toggle.
- Per device, optionally **manage pairing** (pair on connect / unpair on disconnect) —
  required for devices like the Apple Magic Trackpad that won't reconnect cleanly otherwise.
- A **menu bar UI** to see state, toggle devices, and open settings.
- Robust against **sleep/wake** (behave as "unselected" on sleep so another host can grab the device).

## 2. Decisions (confirmed)

| Area | Choice |
|------|--------|
| Bluetooth backend | Native **IOBluetooth** (classic BT, which covers HID devices like trackpads) |
| UI framework | **SwiftUI `MenuBarExtra`**, minimum **macOS 13 (Ventura)** |
| Source → device mapping | **One global source** drives all enabled BT devices (matches prototype) |
| Distribution | **Personal / run from Xcode** now; codesign + notarize later (see §10) |

## 3. Architecture

A small set of `ObservableObject` managers behind protocols, wired in the App entry point.

```
AutoSwitchKVMApp (@main, MenuBarExtra)
 ├─ USBMonitor          IOKit matching notifications: device add/remove + enumeration
 ├─ BluetoothController  IOBluetooth: connect / disconnect / isConnected / pair / unpair
 ├─ SelectionEngine      source presence → debounce → connect/disconnect enabled devices
 ├─ SleepWakeMonitor     NSWorkspace will-sleep / did-wake
 └─ ConfigStore          Codable settings persisted to Application Support (JSON)
```

Key design carryovers from the prototype: a **serialized async task queue** for Bluetooth
calls (no overlapping operations), **per-call timeouts**, **debounced** source evaluation, and
**bounded connect retries**.

### Data model

```swift
struct USBSource: Codable, Hashable {        // the chosen switcher
    var vendorID: UInt16                       // e.g. 0x05E3
    var productIDs: [UInt16]                    // e.g. [0x0626, 0x0610] — a hub can expose several
    var name: String
}

struct BTDevice: Codable, Identifiable {
    var id: UUID
    var name: String
    var address: String                        // BT MAC, e.g. "3C-50-02-BF-22-45"
    var enabled: Bool                          // managed or not
    var managePairing: Bool                    // pair on connect / unpair on disconnect
}

struct AppConfig: Codable {
    var source: USBSource?
    var devices: [BTDevice]
    var debounceMs: Int          = 1200
    var connectRetryMax: Int     = 6
    var connectRetrySecs: Int    = 5
    var btCallTimeoutSecs: Int   = 5
    var showNotifications: Bool  = false
    var launchAtLogin: Bool      = false
    var dockAutoHide: Bool       = false   // Extras: hide Dock when built-in display only
}
```

## 4. USB monitoring (`USBMonitor`)

- Use IOKit `IOServiceAddMatchingNotification` with `kIOFirstMatchNotification` and
  `kIOTerminatedNotification` on `IOUSBDevice` / `IOUSBHostDevice` to get add/remove events.
- Read `idVendor`, `idProduct`, and product name from each `io_object_t`.
- Provide a live `enumerateDevices()` for the settings dropdown, plus a published stream of
  add/remove events for the engine.
- On launch and on wake, **seed** current device presence (prototype's `seedAttached`).

## 5. Bluetooth control (`BluetoothController`)

Protocol so the backend is swappable:

```swift
protocol BluetoothController {
    func isConnected(_ addr: String) async -> Bool
    func connect(_ addr: String) async throws
    func disconnect(_ addr: String) async throws
    func pair(_ addr: String) async throws
    func unpair(_ addr: String) async throws
    func pairedDevices() -> [(name: String, address: String)]   // for settings dropdown
}
```

Native implementation:
- `IOBluetoothDevice(addressString:)` → `openConnection()` / `closeConnection()` for
  connect/disconnect; `isConnected` from the device object.
- `IOBluetoothDevice.pairedDevices()` to populate the "add device" picker (so the user selects
  a known device instead of typing a MAC).
- **Pair**: `IOBluetoothDevicePair` (public API) to initiate bonding.
- **Unpair**: ⚠️ no public API. See risk §9 — handled as the first spike.

Wrap all calls in a serialized actor/queue with timeouts, mirroring `runBlueAsync`.

## 6. Selection engine (`SelectionEngine`)

Mirrors the prototype's state machine:

- Track presence of any of the source's product IDs.
- Debounce events (default 1200 ms) before evaluating.
- On **selected** (source present): for each enabled device → power on adapter → (pair if
  `managePairing`) → connect, with bounded retries until `isConnected` or `connectRetryMax`.
- On **unselected** (source absent): for each enabled device → disconnect → (unpair if
  `managePairing`).
- Guard against overlapping connect runs; abort mid-flight if selection flips.

## 7. UI

**Menu bar (`MenuBarExtra`)**
- Icon reflects state (selected vs unselected / connecting).
- Shows current source, each managed device with a live status dot + enable toggle.
- "Settings…" and "Quit".

**Settings window**
- **Source**: dropdown of live USB devices (name + `VID:PID`), with a refresh button.
- **Devices**: add/remove table. "Add" pulls from paired Bluetooth devices. Each row:
  name, address, `enabled` toggle, `managePairing` (pair/unpair) toggle, manual connect/disconnect test buttons.
- **General**: debounce, retry count/interval, notifications on/off, launch at login.

**Extras**
- **Dock auto-hide** (opt-in, default off): when enabled, hide the Dock when only the
  built-in display is present and show it when an external display connects (ports the
  prototype's `updateDock` via an `NSScreen` change watcher). Lives here because it's
  unrelated to the KVM/Bluetooth core.

## 8. Persistence, lifecycle, system integration

- `ConfigStore`: encode `AppConfig` to JSON in `~/Library/Application Support/AutoSwitchKVM/`.
- **Sleep/wake** (`SleepWakeMonitor`): on will-sleep, proactively disconnect (and unpair where
  configured) so the other host can take the device; on wake, re-seed and re-evaluate.
- **Launch at login**: `SMAppService.mainApp` (macOS 13+).
- Notifications via `UNUserNotificationCenter` (optional, default off).
- **Dock auto-hide** (Extras setting, default off): `NSScreen` change watcher toggles
  `com.apple.dock autohide` — show Dock with an external display, hide it on built-in only.

## 9. Risks & mitigations

- **Native unpair has no public API** (highest risk). **Decided approach:** spike the private
  IOBluetooth call `blueutil` relies on first (Phase 0); if it proves unreliable or breaks across
  macOS versions, fall back to bundling a tiny `blueutil` helper invoked **only** for unpair. The
  `BluetoothController` protocol keeps either choice isolated from the rest of the app.
- **Programmatic pairing of HID devices** may behave differently per device (PIN/secure simple
  pairing). Validate with the Magic Trackpad in the same spike.
- **App Sandbox** restricts IOKit/IOBluetooth. Run **non-sandboxed** for personal use; revisit
  for distribution (§10).
- **USB hubs expose multiple product IDs** — model `productIDs` as a set, as the prototype does.

## 10. Distribution (future)

- Add a Developer ID, codesign the `.app`, and notarize for distribution outside the App Store.
- Add required entitlements / `Info.plist` usage strings (e.g. `NSBluetoothAlwaysUsageDescription`).
- Decide on sandbox vs Developer-ID-only at that point (IOBluetooth + IOKit favor non-sandbox).

## 11. Milestones

0. **Spike**: native connect/disconnect + pair/unpair against the Magic Trackpad; settle the unpair approach.
1. **Scaffold**: Xcode project, `MenuBarExtra` shell, config model + persistence.
2. **USB**: `USBMonitor` with live enumeration + add/remove events; source dropdown.
3. **Bluetooth**: `BluetoothController` (serialized queue, timeouts, retries).
4. **Engine**: wire source presence → connect/disconnect enabled devices, debounced.
5. **Settings UI**: source picker, device table, general settings.
6. **Robustness**: sleep/wake, notifications, launch at login.
7. **Polish**: status icons, error surfacing, edge cases.
8. **Later**: codesign + notarize.
