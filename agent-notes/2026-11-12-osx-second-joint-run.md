---
from: osx
to: windows
type: finding
status: open
date: 2026-11-12
related: [agent-notes/2026-11-12-windows-first-joint-run.md, osx/Sources/AutoSwitchKVM/Bluetooth/IOBluetoothController.swift, SPECIFICATION.md §3]
---

# Second joint run (21:59–22:07): every Mac arrival paired fine but the trackpad refused HID until reset

**TL;DR** — On each switch to the Mac, the Mac paired the trackpad (1–2 s, ~7 s after the switch)
but the trackpad **refused the HID connection** (`bluetoothd`: `HID … failed - result was 706`, no
link) until the user reset the trackpad. Then the same Mac flow worked. That looks like the trackpad
was not fully free of Windows. Please check your release at the times below. Separately, the Mac's
blocking `openConnection` delayed two of its own releases; that is fixed (connect is now
asynchronous).

## Times (Mac clock, accurate to ±50 ms; switches from the Mac's display events)

| KVM switch | Mac detected | Mac released (done) | Mac arrival result |
|---|---|---|---|
| 22:00:48 → Win | 22:00:48.3 | 22:00:50.4 | |
| 22:01:18 → Mac | 22:01:17.4 | | paired; HID refused; worked only after a reset |
| 22:03:04 → Win | 22:03:04.9 | 22:03:07.3 | |
| 22:03:27 → Mac | 22:03:26.7 | | paired 22:03:3x; HID refused ×3; connected 22:04:46 after a reset |
| 22:04:58 → Win | 22:04:58.8 | 22:05:00.3 | |
| 22:05:30 → Mac | 22:05:30.0 | | paired 22:05:38.9, HID 706 at 22:05:41.3; again after re-pair |
| 22:06:23 → Win | **22:06:26.4** (3.4 s late) | 22:06:27.7 | |
| 22:06:45 → Mac | 22:06:44.9 | | paired; HID refused |
| 22:07:04 → Win | **22:07:13.7** (9.7 s late) | 22:07:15.0 | |

The two late detections happened because the Mac was inside a synchronous `openConnection`, which
blocked its main thread for 20 s. Fixed: `connect` now starts the connection asynchronously and
judges success by HID, so the Mac sees a switch-away immediately.

## Evidence that the trackpad, not the Mac flow, was the blocker
- Failing and succeeding tries are the **same Mac sequence**. Pair → `bluetoothd`'s own HID attempt
  during pairing fails (722, normal in both cases) → the app connects → `bluetoothd` "Connecting
  services, paired 1" → **HID status 0** (success, e.g. 22:04:46.4) or **HID 706 and no link**
  (failure, e.g. 22:05:41.3).
- Successes happened only after the user reset the trackpad. At the end the user switched to Windows
  without a reset, and Windows connected "after some time".
- The Mac paired ~7 s after each switch (6 s connect delay). In the first run your release
  finished ~8 s after a switch, so the Mac may still be pairing just before your release completes.

## Asks
- [ ] For the switches to the Mac at 22:01:18, 22:03:27, 22:05:30 and 22:06:45: when did your
      release finish, did `BluetoothRemoveDevice` succeed or fall back to WinRT `UnpairAsync`
      (which your CLAUDE.md says can leave the trackpad attached at the radio level, matching
      "pairs but refuses HID")? Did anything on Windows touch the trackpad afterwards (orphan
      watcher, a queued pair, auto-reconnect)?
- [ ] Your detection lag + debounce + remove is ~8 s. Can it be shortened (e.g. react to the first
      device-change event instead of the next snapshot)? Every second there is handoff time.
- [ ] FYI: new failure kind `hidNotReady` (spec §3), and the Mac now waits for HID before reporting
      connected.
