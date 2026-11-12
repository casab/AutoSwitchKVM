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
| `locationIDs` | Set\<UInt32\>? | **macOS only, optional.** USB bus positions (IORegistry `locationID`) of the source devices. Needed when the same IDs also exist on a hub tier that never switches: on 2026-11-12 a dock kept Genesys `0x0610`/`0x0626` attached permanently, and the Mac counted itself as selected at startup while the KVM was on Windows. Learn / manual pick fill it in; absent = match by IDs only. Windows ignores it (its equivalent would be PnP instance paths) |

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
| `debounceMs` | int | `400` | **Departure** debounce (was `1200` before 2026-11-12) |
| `arrivalDebounceMs` | int | `400` | **Arrival** debounce (snappier connect) |
| `connectRetryMax` | int | `6` | Max connect attempts per handoff |
| `connectRetrySecs` | int | `2` | Backoff **base** seconds |
| `btCallTimeoutSecs` | int | `15` macOS / `45` Windows | Per Bluetooth-call timeout. Platform default differs by design: a Windows pair legitimately takes ~14 s and must absorb the other-host-release race |
| `showNotifications` | bool | `false` | Connect/disconnect alerts |
| `notifyUnexpectedDisconnect` | bool | `false` | Alert on passive drops |
| `autoReconnectOnDrop` | bool | `true` | One rate-limited automatic reconnect after a passive drop (§3) |
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
5. **Reconcile on seed** (Windows and macOS implemented 2026-11-12). A seed (launch, wake,
   unpause, adapter on, profile switch, config change) clears the latch, so step 4 can never fire
   after it. Therefore, if the first evaluation after a seed finds the source **absent** (and not
   paused/suspended), release the enabled devices this host may still hold: disconnect the
   connected ones and unpair the `managePairing` ones (unpair is idempotent). Do it quietly: no
   "Disconnected" notification unless the device was actually connected. Without this, "pause →
   switch to the other host → unpause", or the app starting while switched away (crash, reboot),
   left the device bonded here and the other host locked out. Consequence: a manual connect made
   while switched away is released by the next seed (the KVM decides).

### Connect flow (per enabled device, in list order)
- Apply `connectDelayMs` first (stagger), re-checking cancellation after the wait.
- Retry loop up to `connectRetryMax`:
  - If Bluetooth adapter is **off** → set status `bluetoothOff`, stop.
  - Abort if the run is stale (token changed) or the source is no longer present.
  - If `managePairing`: before pairing, wait for any unpair of this device still in flight (a
    superseded release) and until ~2 s have passed since the last one returned (macOS: the system
    deletes the key ~1.5 s after the link drops; a pair inside that window collides with it). Then
    **pair**, keeping the pairing link up (don't cancel/close the pairing
    operation after success) → wait up to ~3 s for HID on that link; if it comes up, done →
    otherwise **drop** the post-pair connection → short settle (~0.8 s) → **connect**.
    (macOS, 2026-11-12: closing the pairing link while the stack was still bringing HID up made
    it re-page at once; the trackpad's HID on that link was refused with a security failure, and
    the trackpad then refused this host's HID until it bonded elsewhere or was power-cycled.
    Windows: its path never closed the link (the post-pair "disconnect" is a no-op for Classic
    HID); since 2026-11-12 it follows this flow too, skipping the idle drop + 0.8 s settle when HID
    is already up on the pairing link.)
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
- Every manual action goes **through the engine**, never straight to the Bluetooth controller: it
  marks the device busy, supersedes in-flight automation on that device, and sets the device's
  status explicitly before clearing busy. Otherwise the stack's late "disconnected" event after a
  manual Disconnect/Unpair looks like an unexpected drop, and auto-reconnect undoes the user's
  action (seen on Windows; same code pattern existed on macOS).

### System sleep / wake
- On sleep the engine **halts automation first** (supersedes in-flight runs; no evaluation,
  no auto-reconnect while suspended), then releases the enabled devices through the same
  busy/status bookkeeping as an automation release. Releasing straight through the controller let
  the release's own disconnect events look like unexpected drops, and auto-reconnect re-paired the
  device during sleep. On wake: re-enable automation and re-seed. (macOS: `prepareForSleep()` /
  `resumeAfterWake()`; Windows: `NotifySuspending()` / `NotifyResumed()`.)

### Pause
- `paused` suspends automatic connect/disconnect (presence still tracked; manual actions still
  work). Resuming re-seeds so it reconciles to the current state, including releasing devices
  when the source is absent (Evaluate step 5).

### Bluetooth power awareness
- Track adapter power. While **off**: skip connect attempts, show `bluetoothOff` status, reflect in
  the tray icon/header. On **off → on**, re-seed and re-evaluate (reconnect if the source is present).

### Live status & unexpected-disconnect
- Reflect each device's real connection state via the controller's connect/disconnect events, plus
  a slow (~10 s) poll as a safety net.
- If a managed device drops to disconnected **while the source is present, the adapter is on, and
  we're not mid-transition**, treat it as an **unexpected disconnect**: always log it; raise a
  notification if `notifyUnexpectedDisconnect` is on.
- If `autoReconnectOnDrop` is on (default), additionally schedule **one** automatic reconnect for
  the dropped device: short delay (~2 s), re-check source-present/power/pause/busy, then run the
  normal connect flow under the current run token (a newer run supersedes it). Rate-limit to one
  auto-reconnect per device per ~30 s so a flapping device cannot cause a reconnect storm.
  Trade-off: if the *other* host legitimately claimed the device this may briefly contend — the
  source-present guard makes that rare, and the option is user-disableable.

### Stale-bond recovery (connect retries)
- A bond record can outlive the device's willingness to honor it (the other host re-paired the
  device, or it was power-cycled) — observed on hardware as "pair skipped (already paired)"
  followed by connect timeouts on every retry. For `managePairing` devices, retry attempts ≥ 2
  must **unpair (best-effort) and re-pair from scratch** instead of trusting the platform's
  "already paired" state, so the retry loop can always recover without manual intervention.

### Failure classification (connect retries) — implemented on Windows and macOS
- The Bluetooth controller may classify a failure: `deviceBusy` (device answered but refused —
  for an exclusive-bond device, the other host still holds it), `operationInProgress` (an earlier
  OS operation is still running), `timeout`, `authenticationFailed` (stale/mismatched key),
  `deviceNotFound`, `adapterUnavailable`, `invalidAddress`, `hidNotReady`, or `unknown`. Unclassified failures
  are `unknown`, which gets exactly the generic behavior above — so a backend that doesn't
  classify (currently macOS) behaves as before.
- **Soft** failures (`deviceBusy`, `operationInProgress`) retry on a short fixed interval (~3 s),
  **not** counted against `connectRetryMax`, bounded (~20 soft retries); beyond that they count
  as hard. (Observed on hardware: right after a KVM switch the trackpad refuses pairing for
  ~20 s while the Mac releases it; exponential backoff overshoots that window.)
- **Hard** failures consume an attempt and back off exponentially. The stale-bond re-pair applies
  only after a hard failure that may mean a stale bond (`authenticationFailed`, `timeout`,
  `unknown`) — not after a refusal or a missing device.
- `invalidAddress` gives up immediately; `adapterUnavailable` refreshes adapter power first.
- **`hidNotReady`** (macOS: a registered `IOHIDDevice`; Windows: the `BTHENUM` HID-service node and
  at least one of its `HID\` collections started; both engines ask through the controller's
  `isHIDReady`, wait ≤ 3 s on the pairing link, then ≤ 5 s after a connect): the
  link came up but the device never started HID. It is hard, does **not** trigger the stale-bond
  re-pair, and **2 of them end the connect run early** with a "power-cycle the device" message.
  Seen on hardware 2026-11-12: a 1 ms `openConnection` on a bare link was reported as connected and
  idled out 15 s later. The trackpad, paired while the other host was still releasing it, never
  started HID again until power-cycled; the 3 minutes of re-pair churn that followed only made
  things worse.
- **Success means the device is usable, not just linked:** after the connect call, wait (bounded,
  ~5 s) for the HID connection; a platform that can't tell falls back to the link check.
- After a stale-bond unpair, **settle before re-pairing** (~2 s). macOS finishes an unpair up to
  ~2.6 s after the call returns; a pair started right away failed. The stale-bond re-pair runs
  **once per hard failure**, not again on each soft retry.
- Re-check staleness after the pair step and after a failed try (a release may have started).
- **A pair or connection that completes after a release was requested is undone** (unpair /
  disconnect again). A stale run can be mid-ceremony when the source leaves; if its pair lands
  after the release, this host holds the exclusive bond while switched away and the other host
  cannot pair. (macOS: the engine undoes it, and `unpair` also cancels a ceremony still in flight.
  Windows: `WinRtBluetooth` tracks the timed-out "orphan" pairing and unpairs it again.)
- Give-up status and notification carry the **last error**. Release: a failed unpair is retried
  once; a final failure sets `error` and raises a "Release failed" notification (the bond is what
  blocks the other host, so this must not fail silently).
- Every attempt, step outcome (with elapsed time), backoff, abort reason, and give-up is logged.
- Platform code mapping. **macOS** (`osx/…/Bluetooth/BluetoothErrors.swift`, `BTCode`): IOBluetooth
  returns IOKit `IOReturn` values or raw HCI error codes. `deviceBusy`: HCI 0x09, 0x0D–0x0F
  (connection rejected), 0x14, 0x18 (pairing not allowed), `kIOReturnExclusiveAccess`,
  `kIOReturnNoResources`. `operationInProgress`: `kIOReturnBusy`, HCI 0x0B, 0x0C, 0x23, 0x2A, 0x38,
  0x3A. `timeout`: `kIOReturnTimeout`, `kIOReturnNotResponding`, HCI 0x04 (page timeout), 0x08,
  0x10, 0x22, 0x3E. `authenticationFailed`: HCI 0x05, 0x06 (PIN or key missing), 0x25, 0x26, 0x2F.
  `deviceNotFound`: `kIOReturnNoDevice`/`NotAttached`/`NotFound`, HCI 0x02, 0x15.
  `adapterUnavailable`: `kIOReturnNoPower`/`Offline`/`NotReady`, HCI 0x03. On macOS the stale-bond
  case shows up as `kIOReturnTimeout` from `openConnection` (seen on hardware in July 2026), so
  `timeout` must stay re-pair-eligible. **Windows**: `WinRtBluetooth.KindFor`.

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

**Handoff timing (hardware, 2026-11-12).** (1) After a host releases the trackpad, the trackpad
keeps trying to reconnect to that host for several seconds before it accepts a new pairing (the Mac
saw it connect in at the same instant Windows was refused). (2) **The arriving host must not pair
before the leaving host's release has finished.** Windows needs ~8 s from the switch (detect +3.2 s,
debounce 1.2 s, `BluetoothRemoveDevice` 3.4 s). When the Mac paired 2.7 s after the switch, pairing
succeeded but HID never came up and the trackpad wedged. Until a LAN release-ack exists, the arrival
side waits (macOS: the device's connect delay, now 6 s).
*Superseded later on 2026-11-12:* that wedge was the Mac closing the pairing link (see Connect flow),
not the overlap. With the link kept, the user ran both apps with **no connect delay and 400 ms
arrival/departure debounces**, and handoffs worked. Windows' release times show Mac pairings
starting while `BluetoothRemoveDevice` was still running. Defaults are now `connectDelayMs` 0 and
`debounceMs` 400.

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
- A **persistent daily log file**, because handoff bugs are diagnosed across several switches and
  restarts. Windows: `%LOCALAPPDATA%\AutoSwitchKVM\logs\app-yyyyMMdd.log`. macOS:
  `~/Library/Logs/AutoSwitchKVM/app-yyyyMMdd.log` (14 days kept; Diagnostics ▸ Show Log Files).
  The macOS unified log is not a reliable record here: a real run on 2026-11-11 left none of the
  app's own lines in the log store.
- The **handoff timeline** is logged on both sides so each host can measure the other: when the
  source appears/disappears (per source id), when the debounced run starts, and when each device is
  released/connected, with elapsed times.

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
