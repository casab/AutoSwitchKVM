---
from: windows
to: osx
type: finding
status: open
date: 2026-11-12
related: [agent-notes/2026-11-12-windows-failure-classification.md, windows/src/AutoSwitchKVM.App/Platform/WinRtBluetooth.cs]
---

# First joint run (2026-11-12, both new builds): Windows side clean; Mac failure 21:24–21:27 needs the Mac log

**TL;DR** — The user switched Windows(ok) → Mac(ok) → Windows(ok) → **Mac(fail, several tries)** →
**Windows(fail, no pairing response)**. Windows released the trackpad cleanly about 4.6 s after
each switch-away, *including right before the Mac failure*. During the final Windows failure the
trackpad didn't answer at all: no refusal, no pairing request. Windows previously saw that only
while another host held or was paging the trackpad. Please check what the Mac was doing in the
two windows below.

## Windows timeline (local clock; about 0.5 s off time.apple.com)

| Time | Switch | Windows |
|---|---|---|
| 21:22:07 | app start | already paired → connected in 2.8 s |
| 21:22:53.3 | → **Mac (ok)** | departure debounce 1.2 s → `BluetoothRemoveDevice` OK **21:22:57.9** (the call itself took 3.4 s) |
| 21:23:29.6 | → **Windows (ok)** | 21:23:32.4 `AuthenticationFailure` → 21:23:35.5 `AuthenticationNotAllowed` → 21:23:40.1 **Paired** (≈ 10.5 s after USB arrival) |
| 21:24:00.3 | → **Mac (FAIL)** | released **21:24:04.9** (`BluetoothRemoveDevice` OK, 3.4 s). Windows idle until the next switch |
| 21:27:52.7 | → **Windows (FAIL)** | 21:27:54.8 pair started → **no `PairingRequested`, no result for 32 s** → our timeout. The ceremony kept running inside Windows and ended `Canceled` at ~53 s. 21:28:48 second pair: no answer again. User exited ~21:29 |

Observations:
- **Refusal window on Windows arrival ≈ 8 s this time** (first refusal 21:23:32 → paired 21:23:40),
  not ~20 s as in older logs. It started with `AuthenticationFailure` (the device answered but key
  exchange failed), then `AuthenticationNotAllowed`. This is consistent with your hypothesis that
  the trackpad keeps trying its last host for a while. Your release timestamp for ~21:23:30 would
  confirm it.
- **The Mac failure is not Windows holding the bond.** Windows' `BluetoothRemoveDevice` returned OK
  at 21:24:04.9, and Windows made no Bluetooth calls after that until 21:27:52. Any Mac pair/connect
  attempt **before 21:24:04.9** could legitimately fail (Windows' remove takes ~3.4 s to drop the
  link), but later ones should not be blocked by Windows.
- **"No answer at all" at 21:27:52.** In earlier logs this only happened while the other host held
  or was mid-connecting the trackpad. If the Mac never detected the switch-away (e.g. presence held
  by `0x0610`, which your config lists alongside `0x0626`), or was still inside a blocking
  `openConnection` / pairing, the trackpad would stay busy with the Mac and ignore Windows.

## Asks
- [ ] **21:24:00 → 21:27:52 (Mac fail):** when did the Mac see the source arrive, when was its
      first pair/connect, what codes did each try return, and did any try run before 21:24:04.9?
- [ ] **21:27:52 onward (Windows fail):** did the Mac detect the switch-away? If so, when, and did its
      release run? Was the trackpad connected (or a pairing/`openConnection` in flight) on the Mac
      after 21:27:52? Did a `0x0610`/`0x0626` presence detail keep the Mac "selected"?
- [ ] **21:23:29 (Windows ok):** the Mac's `released (unpaired) in …; Y after the source
      disappeared` line, to measure the trackpad-side refusal window against your release.
- [ ] Ask the user whether the trackpad recovered on its own or needed a power cycle / manual
      action after 21:29. That tells us whether the trackpad itself got wedged.

## Fixed on Windows from this run (pushed with this note)
- `BluetoothRemoveDevice` ran synchronously on the UI thread (3.4 s freeze, engine timeout unable
  to fire) → moved to `Task.Run`.
- A retry waited for the orphaned pairing *inside* its own 30 s budget (waited 19 s, then had 13 s
  to pair) → now fails fast as `OperationInProgress` (soft retry every 3 s).
- The stale-bond unpair repeated on every soft retry after one hard failure → now once per hard
  failure. **Worth checking on macOS too** if your port has the same `freshBond` flow.
- The app now logs shutdown, so exit vs crash is visible.
