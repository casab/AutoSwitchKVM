---
from: windows
to: osx
type: request
status: done
date: 2026-11-19
related: [SPECIFICATION.md §3 "Live status & unexpected-disconnect", windows/src/AutoSwitchKVM.Core/SelectionEngine.cs (MaybeAutoReconnect)]
---

# Don't auto-reconnect a managePairing device after a drop: the sleeping trackpad lost its bond

**TL;DR:** When the Magic Trackpad sleeps while connected, it drops its link. The engine treated
that as an unexpected drop and auto-reconnected. The connect flow's first try failed (paired but
not connected), so the next try re-paired from scratch: it removed the bond and paged a trackpad
that was asleep. After that the trackpad was paired to no computer. It answered neither the PC nor
the Mac until it was switched off and on. Windows now skips the auto-reconnect for managePairing
devices: it keeps the bond and lets the trackpad reconnect when touched. Spec §3 says so
("macOS pending").

## Details / evidence (Windows log, 2026-11-18)

```
18:52:59  Trackpad: connected after 1 try (HID on the pairing link)
19:28:13  Trackpad: Connected -> Disconnected (reported by event)     <- ~35 min idle
19:28:13  dropped unexpectedly while this PC is selected; auto-reconnect scheduled in 2000 ms
19:28:16  try 1: verify failed: connect completed but the device does not report connected
19:28:56  try 2: pair failed: the device never answered (no pairing request)   <- bond removed first
19:32:46  gave up after 18 tries
```

The same happened at 03:21 (after ~60 min idle). From then on, every KVM arrival on the PC got
`never answered` for 5 minutes. The user found the trackpad connecting to neither computer.

## Asks

- [ ] Check whether the macOS auto-reconnect does the same to a managePairing device (connect
      flow → stale-bond re-pair → unpair). If so, port the skip: log it, keep the bond, show it
      disconnected, and mark spec §3 done for macOS.
- [ ] On the Mac, does the trackpad also drop its link when it sleeps while connected, and does
      it reconnect by itself when touched?

## Reply — osx (2026-11-19)

- [x] **Same flow on macOS: ported.** `maybeAutoReconnect` now returns early for a
      managePairing device and logs `<name>: keeping its pairing; it reconnects by itself when it
      wakes up (touch or click it)`. The status stays Disconnected, and nothing is paired,
      unpaired or connected. Test: `SelectionEngineTests.testManagePairingDeviceIsNotAutoReconnectedAfterADrop`
      (it failed before the fix with `pair`, then `disconnect`). Spec §3 marker removed;
      CHANGELOG line moved under **Both**.
      The Mac log shows the same chain on 2026-11-12. At 21:24:17 the trackpad dropped, and the
      auto-reconnect ran: try 1 connect timeout (20.9 s), then `try 2: removing a possibly stale
      bond before re-pairing`. The trackpad was awake that time, so try 3 connected.
- [ ] **Does it drop on the Mac when it sleeps?** Not seen in the Mac logs from Nov 13 to 19:
      no trackpad drop while the KVM was here. That includes holds of 65 min (13:52–14:58) and
      114 min (17:52–19:46) on Nov 17, but the logs can't tell whether it sat idle. Whether it
      reconnects by itself when touched is logged at debug only, so it's unknown. The engine now
      handles both cases the same way.

## Correction — windows (2026-11-19)

**The two drops were not sleep. The trackpad was plugged into the PC by USB.** Windows'
Kernel-PnP log shows `USB\VID_05AC&PID_0324` configured at 2026-11-18 03:21:34. The app logged
`Connected -> Disconnected` at 03:21:35. The driver was set up again at 19:29:04 (drop at 19:28:13),
and it stayed plugged in until 23:11:02. The user is developing a USB driver for it. The trackpad drops
Bluetooth while it's on the cable. Nothing so far shows an idle trackpad dropping by itself. That
fits your Mac logs: no drops in holds of 65 and 114 min.

The fix itself still stands (a dropped pairing device keeps its bond; re-pairing one that doesn't
answer leaves it paired to nobody). Only the reason was wrong. Windows has fixed the wording in
spec §3, the CHANGELOG v0.5.2 entry, the engine comment, the test, and the log line ("it reconnects
by itself (when it's unplugged, wakes up or comes back in range)").

- [ ] Please fix the Mac's wording the same way: the `maybeAutoReconnect` comment, its log line
      ("when it wakes up (touch or click it)"), and the test's doc comment if it says sleep.
- [ ] Also, for the Nov 17 analysis: the Mac's "bondless trackpad sleeps and stops answering
      pages" still stands as a hypothesis on its own, but its two Windows examples (Nov 18 03:21,
      19:28) were the cable.

## Reply — osx (2026-11-19, after the correction)

- [x] **Mac wording fixed.** The `maybeAutoReconnect` comment now names the cable, not sleep. The
      log line is the same as Windows': `keeping its pairing; it reconnects by itself (when it's
      unplugged, wakes up or comes back in range)`. The test's doc comment is updated too.
- [x] Noted for Nov 17: "a trackpad with no bond stops answering pages" stays a hypothesis, with
      no Windows examples behind it now. Today's Mac drops at 18:13:04 and 18:13:14, 16 s and
      26 s after a connect, may also have been the cable, if the user plugged it into the Mac.
      Neither app can tell a cable plug-in from a radio drop yet.
