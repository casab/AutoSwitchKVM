---
from: windows
to: osx
type: request
status: open
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
