# AutoSwitch KVM — Improvements & Roadmap

Current state: a working menu bar app with named **profiles** (each a source + device set),
Learn-source detection, Bluetooth connect/disconnect (+ optional pair/unpair), real-time status via
IOBluetooth notifications, and sleep/wake handling. UI is a custom menu bar panel (status, device
cards, Pause, connect/disconnect-all, profile switch) plus a tabbed Settings window
(Source / Devices / General / Extras / Diagnostics) with config import/export and an app icon.
It's self-signed (ad-hoc), CI runs build + tests, and the core (engine, config, models) is
unit-tested with mocks.

This document tracks what's still **outstanding**; completed items are pruned from it as they land
(see "Done so far" in §5 for the running tally).

## 1. Engineering quality

- **Make lint a hard gate.** The CI lint job is advisory (`continue-on-error`) and `swift-format`
  runs `--strict`; once a clean local `swift-format lint` is confirmed, flip it to blocking
  (`continue-on-error: false` in `.github/workflows/ci.yml`).
- **Config migration/versioning.** Add a `schemaVersion` to `AppConfig` so future shape changes
  decode old files gracefully instead of resetting to defaults.
- **Error surfacing.** Promote `.error` statuses into a visible, dismissible banner in Settings,
  not just a log line.
- **Optional: full module split.** The logic currently lives in the app target and is tested via
  `@testable import`. If a hard boundary is ever wanted, extract an `AutoSwitchKVMCore` library
  target (requires making the used types `public`).

## 2. Robustness / correctness

Done (all shipped):
- **Adapter power awareness** — tracks `bluetoothPowered`, skips connect attempts and shows a
  `.bluetoothOff` status + menu/icon state while off, re-seeds on power-on.
- **Robust pairing** — full `IOBluetoothDevicePairDelegate` (auto-confirms SSP, step-specific
  failure messages); `PairingHelper` is cancellation-aware.
- **Reliable connect** — uses the **synchronous** `IOBluetoothDevice.openConnection()` for a real
  success/failure result (the async callback proved unreliable: it sometimes never fired even when
  the ACL link came up, leaving a dead HID). The engine treats only a clean, non-throwing connect
  as success and drops partial links before retrying; unpair is judged by `isPaired()`.
- **Smarter reconnect** — exponential backoff `connectRetrySecs * 2^(attempt-1)` (default 2s →
  2/4/8/16/…), ±15% jitter, capped 30s, bounded by `connectRetryMax`; unit-tested.
- **Per-event debounce** — separate `arrivalDebounceMs` (default 400) and `debounceMs` (departure,
  default 1200), tunable in General ▸ Timing.
- **Unexpected-disconnect notice** — opt-in (General ▸ Behavior); fires when a managed device drops
  while the source is present.

Open (optional, on the shelf):
- **`blueutil` fallback** — only if native connect ever proves unreliable, add a
  `BlueutilController: BluetoothController` shelling out to `blueutil`, selectable behind the
  existing protocol (the prototype's proven approach). Kept native by preference.

## 3. Build & distribution

- **Signing & notarization.** Local builds are self-signed (ad-hoc) by default; `project.yml`
  documents switching to Apple Development signing with a Team ID. Still outstanding for sharing
  the `.app`: Developer ID Application signing, hardened runtime (`ENABLE_HARDENED_RUNTIME: YES`),
  notarization via `notarytool`, and stapling.
- **Versioning.** Drive `MARKETING_VERSION` / `CURRENT_PROJECT_VERSION` from a single source; tag
  releases.
- **Auto-update (optional).** Sparkle, if this is ever distributed beyond your own machines.

## 4. Feature backlog (roughly prioritized)

Lower value / nice-to-have:
1. Menu bar icon badge showing how many devices are connected.
2. Sound feedback on transitions.
3. Crash/error log export for debugging.

## 5. Suggested order of work

Done so far: self-signing (ad-hoc), app icon / asset catalog, engineering hygiene (`os.Logger`
logging + exportable Diagnostics log, GitHub Actions CI, `swift-format` config,
`-strict-concurrency=complete`), config import/export, named profiles, per-device connect
order/delay, user-assignable global keyboard shortcuts (off by default), and **the full robustness pass**
(§2: power awareness, robust pairing, reliable synchronous connect, backoff, per-event debounce,
disconnect notice).

Remaining:
1. Flip the lint gate to blocking (concurrency is already at `complete` and clean).
2. Config schema versioning + error-surfacing banner (engineering quality, §1).
3. Developer ID signing + notarization when ready to distribute the `.app`.
4. Lower-value polish (icon badge, sounds) and the optional `blueutil` fallback.
