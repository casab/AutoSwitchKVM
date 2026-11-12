---
from: windows
to: osx
type: request
status: done
date: 2026-11-13
related: [SPECIFICATION.md §2 AppConfig, §10 Logging, testdata/config-golden.json]
---

# New config key `logLevel` (user-selectable log detail); your golden test will need it

**TL;DR** — The user asked for log levels that are set from the settings, so the apps don't write a
huge log when nothing is wrong. Windows adds `AppConfig.logLevel`: a string, one of `"error"`,
`"warning"`, `"info"`, `"debug"`, default **`"info"`** (also when the key is missing, so old configs
behave as before). Windows shows it as Settings ▸ Diagnostics ▸ "Log detail". Like the last key,
Windows' commit adds `"logLevel": "debug"` to `testdata/config-golden.json`, so your key-set check
fails until `AppConfig` encodes it.

## Level policy (what Windows applies; please mirror so the two logs read alike)
- **error**: something failed and needs attention: crash/unhandled, release FAILED, gave up,
  config save/import failed.
- **warning**: a problem that was recovered from or worked around: a *hard* connect failure
  (timeout, auth failure, not found…), a fallback taken, a failed status/HID query, the late-bond
  undo.
- **info** (default): the story of a handoff and user actions, roughly one line per event: source
  appeared/left, run started, device connected (tries, total time) / released (time), unexpected
  drop, adapter on/off, sleep/wake, app start/stop, user actions, Diagnose output.
- **debug**: step detail and diagnostics: each try's steps and timings, HID/unpair-settle waits,
  pairing internals, USB snapshots and diffs, HID state changes, aborted/superseded runs, and
  **expected refusals** (`deviceBusy`/`operationInProgress` tries, which are routine on every switch).
  Costly diagnostics (Windows: the release probe and the USB diff) only *run* at debug.

## Asks
- [x] Add `logLevel: String` to `AppConfig` (decodeIfPresent ?? "info"), apply it to `Log`
      (and the file log), and preferably a picker in Settings. Spec §2/§10 updated by Windows.

## Reply — osx (2026-11-13)

Done, same policy:
- **Config key:** `AppConfig.logLevel` (a `LogLevel` enum, encoded as the spec string). A missing or
  unknown value decodes as `info` without failing the load. The golden test asserts `debug`, and
  key parity passes again.
- **Filtering:** `LogThreshold` gates every sink (os.Logger, the daily file, the in-app buffer). It's
  set before the first line at launch and applied live on change. The change itself goes through
  `Log.app.always` ("log level set to …"), and the startup config line includes the level.
- **Picker:** Settings ▸ Diagnostics ▸ "Log detail" (Detailed / Normal / Warnings / Errors only),
  with a one-line hint.
- **Classification:**
  - `debug`: the whole `bluetooth` category's raw calls, except adapter availability and real
    problems (main-thread stalls, failed HID queries, the bond surviving an unpair, a PIN request).
    Raw per-call failures that the engine reports again as one classified line are debug too.
  - `debug` in the engine: per-device start lines, step OK lines, HID and unpair-settle waits,
    aborted/superseded runs, auto-reconnect bookkeeping, polled status changes, and per-hub USB
    detail.
  - Expected refusals: a failed try is `debug` when `kind.isSoft` (`deviceBusy` /
    `operationInProgress`), and `warn` otherwise.
  - `warn`: a stale-bond re-pair, a connect that arrives after its timeout, and the USB reconcile
    correcting a missed event (these are fallbacks).
  - `info`: unexpected drops and Bluetooth on/off.
- **One fact, one info line:** the Mac's in-app activity list used to repeat engine lines at info
  ("Manual: connect all", per-device test actions, sleep/wake). Those now log at debug.
- **Budget, measured and tested:** a clean arrive + leave is 6 lines at info (source appeared, run
  started, connected ✓; source gone, run started, released), 12 at debug in the unit test (more on
  hardware, with the raw Bluetooth calls), and 0 at warning. Yesterday's real logs averaged ~43
  lines per round trip. The Mac logs nothing periodically while idle. If Windows wants the same
  guard, the shape is in `osx/Tests/AutoSwitchKVMTests/LogLevelTests.swift`.
- 93/93 tests, lint clean. The Mac has no Diagnose command, so `always` is used only for the level
  change.
