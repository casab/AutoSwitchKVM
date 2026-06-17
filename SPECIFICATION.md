# AutoSwitch KVM — Behavior Specification

Platform-neutral specification of what the app does. The macOS app (`osx/`) is the reference
implementation; this document is the contract a port (Windows, later Linux) must meet so behavior
and the settings model stay consistent. Terms are described abstractly; platform-specific APIs are
noted where relevant.

---

## 1. Concept

A background, tray/menu-bar app for a KVM setup. A KVM exposes a USB hub to whichever computer is
currently selected. The app:

1. Watches USB attach/detach to detect a configured **source** (a named set of USB device IDs).
2. When the source **appears** (this machine selected), connects the configured Bluetooth devices.
3. When the source **disappears** (switched away), disconnects them so the other host can use them.
4. Optionally manages **pairing** per device (pair on connect / unpair on disconnect) for devices
   that only work cleanly with one host at a time (e.g. Apple Magic Trackpad).

The app is otherwise idle and unobtrusive: a tray/menu-bar item, no main window.

---

## 2. Data model

All persisted as one JSON config. Field names below are the canonical keys (keep them identical
across platforms so exported configs are portable).

### USBSource
A switcher source = one USB vendor + the set of product IDs that appear when this machine is
selected (a hub often exposes several). Single vendor by design.

| Field | Type | Notes |
|-------|------|-------|
| `name` | string | User-given (e.g. "Desk KVM") |
| `vendorID` | UInt16 | e.g. `0x05E3` |
| `productIDs` | Set\<UInt16\> | e.g. `{0x0626, 0x0610}` |

### BTDevice
A managed Bluetooth device.

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `id` | UUID | new | Stable identity |
| `name` | string | — | Friendly name |
| `address` | string | — | BT MAC (normalize to lowercase, `-` separators internally) |
| `enabled` | bool | `true` | Participates in automatic handoff |
| `managePairing` | bool | `false` | Pair on connect / unpair on disconnect |
| `connectDelayMs` | int | `0` | Per-device stagger before connecting |

Connect **order** = position in the profile's device list (user-reorderable).

### Profile
A named source + device set. The active profile is what the engine uses.

| Field | Type | Notes |
|-------|------|-------|
| `id` | UUID | |
| `name` | string | e.g. "Desk", "Travel" |
| `source` | USBSource? | nil until configured |
| `devices` | [BTDevice] | ordered |

### KeyShortcut
A global hotkey. (macOS stores Carbon modifiers; a port stores its own modifier mask + the same
display string convention, e.g. `⌃⌥⌘P` / on Windows `Ctrl+Alt+Cmd⇒Win` equivalents.)

| Field | Type | Notes |
|-------|------|-------|
| `keyCode` | UInt32 | virtual key code |
| modifiers | UInt32 | platform modifier mask |
| `display` | string | shown in UI |

### AppConfig (top level)

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `profiles` | [Profile] | one "Default" | Never empty |
| `activeProfileID` | UUID | first profile | Must reference an existing profile |
| `debounceMs` | int | `1200` | **Departure** debounce |
| `arrivalDebounceMs` | int | `400` | **Arrival** debounce (snappier connect) |
| `connectRetryMax` | int | `6` | Max connect attempts per handoff |
| `connectRetrySecs` | int | `2` | Backoff **base** seconds |
| `btCallTimeoutSecs` | int | `15` | Per Bluetooth-call timeout |
| `showNotifications` | bool | `false` | Connect/disconnect alerts |
| `notifyUnexpectedDisconnect` | bool | `false` | Alert on passive drops |
| `launchAtLogin` | bool | `false` | Autostart |
| `paused` | bool | `false` | Suspend automation |
| `dockAutoHide` | bool | `false` | **macOS-only** Extra |
| `globalHotkeysEnabled` | bool | `false` | Master toggle for hotkeys |
| `hotkeyPause` / `hotkeyConnectAll` / `hotkeyDisconnectAll` | KeyShortcut? | platform defaults | Cleared = null |

`source` / `devices` are convenience accessors onto the **active profile** (not stored separately).

### Persistence
- One JSON file in the per-user app-support/local-app-data dir (macOS:
  `~/Library/Application Support/AutoSwitchKVM/config.json`; Windows:
  `%LOCALAPPDATA%\AutoSwitchKVM\config.json`).
- Writes are debounced and atomic.
- **Backward-compatible decode:** unknown/missing keys fall back to defaults; never reset the whole
  config because one field is absent. A pre-profiles config (top-level `source`/`devices`) migrates
  into a single "Default" profile. For hotkeys, distinguish *absent* (→ default combo) from
  *present-null* (→ intentionally cleared).
- **Import/Export:** the whole config to/from a JSON file.

---

## 3. Selection engine (the core, platform-portable)

This logic should be identical across platforms — ideally a shared, unit-tested module driven by
two injected dependencies: a **USB monitor** (events + current snapshot) and a **Bluetooth
controller** (the operations in §5). It owns no platform APIs directly.

### Presence & state
- Track which of the active source's product IDs are currently attached (match on vendor+product).
- `selected` (observable) = **raw presence** (any source product ID present). Updated **always**,
  even while paused, so the UI is accurate.
- `automationActed` = private latch for the presence the automation last acted on (distinct from
  `selected`).

### Debounce
- USB add/remove and the initial seed schedule a debounced evaluation.
- Interval is **arrival** debounce when the source is now present, **departure** debounce when now
  absent. (Rationale: connect snappily; tolerate brief USB blips before disconnecting.)

### Evaluate (after debounce)
1. Set `selected = present`.
2. If **paused**, stop here (no connect/disconnect; presence still reflected).
3. If `present && !automationActed` → mark acted, connect all enabled devices.
4. If `!present && automationActed` → clear acted, disconnect all enabled devices.

### Connect flow (per enabled device, in list order)
- Apply `connectDelayMs` first (stagger), re-checking cancellation after the wait.
- Retry loop up to `connectRetryMax`:
  - If Bluetooth adapter is **off** → set status `bluetoothOff`, stop.
  - Abort if the run is stale (token changed) or the source is no longer present.
  - If `managePairing`: **pair** → **drop** the transient post-pair connection → short settle
    (~0.8 s) → **connect**. (The connection a stack reports immediately after pairing can be
    unreliable; a clean reconnect is what actually brings the device up.)
  - Else: if already connected, done; otherwise **connect**.
  - **Success requires a *clean* connect** — the connect call completing without error — **and**
    the device reporting connected. A link-only/ACL state that isn't a working connection does
    **not** count; drop any partial connection and retry.
  - Between attempts wait **exponential backoff**: `connectRetrySecs * 2^(attempt-1)`, ±15% jitter,
    capped at 30 s.
- On giving up after `connectRetryMax`, set status `error`.

### Disconnect flow (per enabled device)
- **Disconnect**; if `managePairing`, then **unpair** (remove the bond). Set status `disconnected`.

### Manual actions (ignore selection/source; act on all configured devices)
- **Connect all now**, **Disconnect all now**.
- Per-device test buttons: **Connect / Disconnect / Pair / Unpair**.

### Pause
- `paused` suspends automatic connect/disconnect (presence still tracked; manual actions still
  work). Resuming re-seeds so it reconciles to the current state.

### Bluetooth power awareness
- Track adapter power. While **off**: skip connect attempts, show `bluetoothOff` status, reflect in
  the tray icon/header. On **off → on**, re-seed and re-evaluate (reconnect if the source is present).

### Live status & unexpected-disconnect
- Reflect each device's real connection state via the controller's connect/disconnect events, plus
  a slow (~10 s) poll as a safety net.
- If a managed device drops to disconnected **while the source is present, the adapter is on, and
  we're not mid-transition**, treat it as an **unexpected disconnect**: always log it; raise a
  notification if `notifyUnexpectedDisconnect` is on.

### Per-device status states
`idle`, `connecting`, `connected`, `disconnected`, `bluetoothOff`, `error`.

---

## 4. Source detection (USB) — platform requirement

The USB monitor must provide:
- A **live snapshot** of attached USB devices (vendorID, productID, name) for the source picker.
- An **event stream** of attach/detach (vendorID, productID, added?) delivered to the engine.
- Prefer **event-driven** detection (no polling). A periodic **reconcile** (~2 s) is recommended as
  a safety net because a KVM switch causes a USB re-enumeration storm that can starve a pure
  debounce. (The Windows prototype proved this is necessary.)

**Switcher nuance (from the prototype):** a KVM hub may expose multiple product IDs where one is a
permanent/always-on instance that stays present even when switched away. The reliable selection
signal is the product ID that actually disappears on switch-away. The source model captures the
relevant IDs; detection should treat presence of *any* configured ID as selected, but implementers
should pick the disappearing ID(s) when learning a source.

---

## 5. Bluetooth operations — platform contract

The engine depends on this surface (the macOS `BluetoothController` protocol). A port implements it
natively:

| Operation | Contract |
|-----------|----------|
| `isConnected(address) → Bool` | Is the device currently connected/usable |
| `connect(address)` | Establish a working connection; throw on failure; return only when truly connected |
| `disconnect(address)` | Tear down the connection |
| `pair(address)` | Bond with the device (auto-confirm secure-simple-pairing; report the failing step) |
| `unpair(address)` | Remove the bond; verify removal (don't trust a fire-and-forget return) |
| `isPoweredOn() → Bool?` | Adapter power state (nil if unknown) |
| `pairedDevices() → [..]` | Known/paired devices for the "add device" picker |
| monitoring | Notify the engine of connect/disconnect transitions for given addresses |

**Important cross-platform discrepancy.** macOS exposes explicit connect/disconnect for a device.
On Windows, Classic-HID devices (like the Magic Trackpad) effectively **connect on pair and
disconnect on unpair** — there is no separate "connect" call. A port maps the contract accordingly
(see `windows/PLAN.md`): for such devices, `connect` ≈ ensure paired, `disconnect` ≈ unpair, and
`isConnected` ≈ `BluetoothDevice.ConnectionStatus == Connected` (primary), corroborated by HID PnP
node presence. The bond is **exclusive**: while one host holds the pairing the other host cannot
connect, so the handoff is not passive — the releasing side must actively unpair. This is why
`managePairing` is effectively mandatory (not optional) for those devices on Windows. Validated
against real hardware in Milestone 0 (`windows/PLAN.md` "spike findings").

---

## 6. Learn source mode

- User starts Learn; the app snapshots currently-attached USB devices.
- User switches the KVM (away and/or back); the app records which devices **appeared or
  disappeared** during the window.
- Present those candidates with checkboxes (pre-checked), so the user can deselect anything not part
  of the switch (e.g. a mouse plugged into the hub) and **name** the source.
- Enforce a single vendor (the source model is one vendor + product IDs).
- A **manual** path also exists: pick from the currently-attached list + name it.

---

## 7. Profiles

- Multiple named profiles; one active at a time.
- Switch from the tray/menu and from a Settings "profile bar"; add / rename / delete in Settings.
- Editing Source/Devices edits the **active** profile.
- Switching re-points the engine at the new profile and re-seeds; it does **not** force-disconnect
  the previous profile's devices.
- Global options (timing, notifications, shortcuts, etc.) are **app-wide**, shared across profiles.

---

## 8. UI surfaces

### Tray / menu-bar panel
- Status header: a colored indicator + text — **Bluetooth off** (if adapter off) / **Paused** /
  **Source active** / **Source inactive** — and the active source name.
- Profile switcher when >1 profile: segmented buttons for ≤3 profiles, an overflow menu for more;
  hidden with a single profile.
- Device rows: type icon, name, live **status pill**, enable toggle.
- Quick actions: **Connect all now**, **Disconnect all now**, **Pause/Resume**.
- Footer: open Settings, Quit.
- Tray icon reflects state (active / inactive / paused / Bluetooth-off).

### Settings window (tabs)
- **Source:** current source (name + IDs + active indicator); **Learn source…**, **Pick manually…**,
  **Clear**.
- **Devices:** add (from paired list or manual name+address); per device card — icon, name, address,
  status pill, enable toggle, **Manage pairing** checkbox, **Connect delay** picker, manual
  Connect/Disconnect/Pair/Unpair test buttons, reorder up/down, delete; plus an Activity log.
- **General:** Timing (arrival debounce, departure debounce, connect retries, retry base interval,
  per-call timeout); Behavior (Pause, Show notifications, Notify on unexpected disconnect, Launch at
  login); Global shortcuts (enable + recorders for Pause/Connect-all/Disconnect-all + Restore
  defaults); Configuration (Export/Import).
- **Extras:** platform conveniences for the two-computer workflow (macOS: Dock auto-hide when on the
  built-in display only). Platform-specific; may differ per OS.
- **Diagnostics:** read-only live state — engine/source status, adapter power, attached USB devices
  (with the source highlighted), paired Bluetooth devices with connection status — plus **Debug
  logs** (view / copy / export / clear).

---

## 9. System integration

- **Sleep/Wake:** on sleep, proactively disconnect (so the other host can take the devices); on
  wake, re-seed and re-evaluate.
- **Launch at login:** OS-native autostart, toggleable.
- **Notifications:** connect / disconnect / gave-up alerts gated by `showNotifications`;
  unexpected-disconnect gated by `notifyUnexpectedDisconnect`. (macOS requires a real `.app` bundle
  for notifications; note per-platform prerequisites.)
- **Global shortcuts:** system-wide hotkeys for Pause/Connect-all/Disconnect-all, off by default,
  user-assignable, with a restore-defaults action. Should not require elevated/Accessibility
  permission where avoidable.

---

## 10. Logging & diagnostics

- A categorized logger (`app` / `usb` / `bluetooth` / `engine`) to the OS log, **mirrored** into an
  in-app ring buffer shown in Diagnostics (view / copy / export). Detailed connect/pair steps are
  logged to aid troubleshooting.

---

## 11. Non-functional expectations

- Event-driven where possible; a low-frequency reconcile/poll only as a safety net.
- Reliable handoff: bounded retries with backoff; never report a false "connected".
- The engine core is unit-tested with mocked USB + Bluetooth (no hardware needed).
- Settings persist immediately and survive field additions (backward-compatible decode).

---

## 12. Platform-specific items (not part of the shared contract)

- **macOS:** Dock auto-hide (Extras); notifications need a `.app`; pairing/connect via
  IOKit/IOBluetooth; native unpair uses a private selector.
- **Windows:** Classic-HID pair=connect/unpair=disconnect semantics; source signal nuance
  (always-on hub instance); reconcile timer necessity; pairing via WinRT. See `windows/PLAN.md`.
