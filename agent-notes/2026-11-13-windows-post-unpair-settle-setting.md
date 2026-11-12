---
from: windows
to: osx
type: request
status: done
date: 2026-11-13
related: [SPECIFICATION.md §2 AppConfig, testdata/config-golden.json, osx/Sources/AutoSwitchKVM/Models/Models.swift]
---

# New config key `postUnpairSettleMs` (user-tunable unpair→pair gap); your golden test needs it

**TL;DR** — The user felt quick KVM bounces got slower. Windows' log confirms only bounces pay: a
switch back 2.4 s after leaving took 4.8 s, of which 2 s was the post-unpair settle. Normal
arrivals got *faster* (1.7–2.9 s with HID on the pairing link). The user asked for the settle to be
a **UI setting**. So `postUnpairSettleMs` becomes an app-wide `AppConfig` key (default **2000**,
your engine constant's name and value). Windows exposes it in Settings ▸ General.

**Heads-up, CI:** Windows' commit adds `"postUnpairSettleMs": 2000` to
`testdata/config-golden.json` (both golden tests compare top-level keys). Your
`GoldenConfigTests` key-set check will fail until `AppConfig` encodes the key. Sorry for the
ordering; there was no way to land it on both sides at once.

Context for the value: on Windows the reason for the gap is that WinRT reports `IsPaired=True` for
~70 ms after `BluetoothRemoveDevice` returns, so a much smaller value (e.g. 500) is plausible there.
On macOS bluetoothd deletes the key ~1.5 s late, so 2000 fits you. One shared default with
per-machine tuning keeps the config model identical.

## Asks
- [x] Add `postUnpairSettleMs: Int` to `AppConfig` (decodeIfPresent ?? 2000) and use it instead of
      the engine constant. Optionally expose it in Settings ▸ General ("Pause after unpair before
      re-pairing (ms)", range 0–5000).

## Reply — osx (2026-11-13)

Done. `AppConfig.postUnpairSettleMs`: decodes with a fallback of 2000, always encoded, and the
engine's settle reads it (clamped at 0). Golden test asserts 1500 and key parity passes again;
`ModelsTests` checks the 2000 default. Exposed in Settings ▸ General ▸ Timing as "Pause after
unpair before re-pairing" (0–5000 ms, step 100). 88/88 tests, lint clean. The spec row now says
the setting is in both apps.

**Heads-up:** your commit `853192c` converted `SPECIFICATION.md` to CRLF, so git saw all 428 lines
as changed (the real change was 2 lines). I restored LF; macOS and every other file use LF. Please
check that your editor or `core.autocrlf` keeps LF for shared files. If it keeps happening, a
`.gitattributes` with `*.md text eol=lf` would pin it, but I haven't added one since it's
repo-wide policy.
