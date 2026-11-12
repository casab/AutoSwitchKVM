---
from: windows
to: osx
type: request
status: open
date: 2026-11-12
related: [osx/Sources/AutoSwitchKVM/Engine/SelectionEngine.swift, osx/Sources/AutoSwitchKVM/Bluetooth/BluetoothController.swift, osx/Sources/AutoSwitchKVM/App/AppController.swift, osx/Sources/AutoSwitchKVM/Models/ConfigStore.swift]
---

# The user reports problems in the macOS app; please run the same diagnose-then-harden pass as Windows

**TL;DR** — The user believes recent changes broke parts of the Mac app. Start from evidence:
build + test, run a real handoff, read the logs. Then do the error-handling audit the Windows side
just did. On Windows, most failed handoffs came from two causes that were invisible until the
failures were logged and classified.

## Suspects

Recent commits that touched `osx/`, none of them validated on hardware as far as Windows can tell:
`806c468` ("Let codex run its magic"), `617edbe` and `d3c3b1a` ("fable worked its magic").
`617edbe` alone changed the engine, `IOBluetoothController`, `SleepWakeMonitor`, `USBMonitor`,
`HotKeyManager` and `ConfigStore`. `git show --stat <sha> -- osx` / `git diff 07a3dbd..HEAD -- osx`
gives the full picture.

## The approach that worked on Windows

1. **Evidence first.** Read the real logs from several handoffs (Diagnostics ▸ Debug logs /
   `log show --predicate 'subsystem == "com.enginal.AutoSwitchKVM"'`). Tally the outcomes. On
   Windows the tally changed the diagnosis: 64 `OperationAlreadyInProgress` vs 7 successes in one
   day pointed straight at a timeout bug, not at the device.
2. **Make every failure visible.** The engine should log each try, step (with elapsed time),
   backoff, abort reason, and give-up with the last error. Replace silent swallowing with logged
   warnings. Give-up and release-failure notifications should carry the cause.
3. **Classify failures** (`SPECIFICATION.md` §3 "Failure classification"). Refusal/busy → soft
   retry; possible stale bond → re-pair; bad address → stop.
4. **Look for races at the edges:** manual actions vs automation, sleep vs auto-reconnect, a pair
   that lands after a release.

## Concrete spots seen from here (from reading the code, not running it)

- **`withTimeout` leaves blocking calls running** (`BluetoothController.swift:72`; the comment says
  "blocking calls run out on their own"). Windows had the same bug class: a timed-out WinRT pair
  kept running, and every retry collided with it. On macOS, a synchronous `openConnection()` that
  outlives its timeout may still be blocking (on the main thread?) while the engine retries or
  starts a release. Check what the next call does while the previous one is still running.
- **Silent `try?` on the engine's best-effort steps:** `SelectionEngine.swift:355` (stale-bond
  unpair), `:362` (post-pair disconnect), `:392` (cleanup disconnect). A failure there is
  currently invisible.
- **`ConfigStore` save failures are swallowed** (`ConfigStore.swift:48-49, 57`): `try? data.write`.
- **Manual test actions and the sleep release bypass the engine** (`AppController.swift:308-347`).
  See the other note (`2026-11-12-windows-failure-classification.md`, items 3-4).

## Asks
- [ ] Build + `swift test`; reply with any failures.
- [ ] Run a few real handoffs in both directions and reply with what breaks (log excerpts).
- [ ] Fix what's broken, then apply steps 2-4 above. Add tests to `SelectionEngineTests` for each
      fix (Windows equivalents: `windows/tests/AutoSwitchKVM.Core.Tests/FailureHandlingTests.cs`).
- [ ] Reply here with what you found and changed (especially the macOS release timing). The
      Windows side tunes its soft-retry window from it.
