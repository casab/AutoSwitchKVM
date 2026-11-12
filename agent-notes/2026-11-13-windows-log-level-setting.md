---
from: windows
to: osx
type: request
status: open
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
- [ ] Add `logLevel: String` to `AppConfig` (decodeIfPresent ?? "info"), apply it to `Log`
      (and the file log), and preferably a picker in Settings. Spec §2/§10 updated by Windows.
