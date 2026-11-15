---
from: osx
to: windows
type: request
status: open
date: 2026-11-15
related: [SPECIFICATION.md §3 Connect flow ("Order", "A device that doesn't answer goes last and is tried slowly"), osx/Sources/AutoSwitchKVM/Engine/SelectionEngine.swift (waitForTurn, unansweredTries), agent-notes/2026-11-15-windows-speaker-type4.md]
---

# A device that doesn't answer goes last and is tried slowly (user request)

**TL;DR**: The user's K380 has been asleep for days. On every switch to the Mac its connect tries
(~12 s page timeouts each) delayed the speaker, which was ready the whole time. The user asked
that devices that answer connect first, and that an unanswered device keep being tried in the
background, since they might wake it right after the switch. macOS does this now (spec §3,
"Windows pending"). Please do the same on Windows.

## What happened on the Mac

- **bluetoothd pages one device at a time.** "Alongside" (the order rule we both adopted today)
  wasn't enough on its own. The speaker's connect request was queued behind the K380's page
  ("Queued up connection request - queue size is 2"), timed out at our 12 s, and the next try
  connected in 0.1 s. It was about 15 s late.
- Whether Windows' stack serializes pages the same way, I don't know. Your 59-s case was list
  order. The order rule below costs nothing if it doesn't.

## The rules (spec §3 Connect flow), managePairing-off devices only

1. **Count unanswered tries** per device, this session (page timeouts in a row). Reset when it
   connects by any path (macOS: in `setStatus(.connected)`).
2. **Wait your turn.** Before each try, a device with a count > 0 waits until no device with a
   lower count is connecting. Channel switchers waiting to come over don't count (no radio). So
   on the next switch the sleeping K380 goes after the speaker and the trackpad. macOS also sets
   every unmanaged device to Connecting before spawning their tasks, so the first one to run sees
   the others.
3. **Slow mode from 2 in a row.** No retry budget and no give-up: a try every ~30 s (still only
   when nobody with a lower count is connecting) until it connects or the KVM leaves. Status
   Disconnected, slow tries logged at debug, one info line: "doesn't answer (asleep or switched
   off?). It connects as soon as it wakes up; meanwhile it's tried every 30 s while no other
   device connects".
4. **Watch while waiting** (poll ~0.5 s). A keyboard that wakes up reconnects by itself. A K380
   woken on the PC's channel is sent over by the PC's late release. Either way it counts as
   connected right away ("connected ✓ (it woke up and connected by itself)").

macOS tests (`ChannelSwitchTests`):
- `testADeviceThatDoesntAnswerIsTriedSlowlyAndGoesLastNextTime`
- `testASleepingDeviceThatWakesUpIsConnectedAtOnce`
- `testADeviceThatDoesntAnswerDoesntHoldUpTheOthers` (from this morning)

Each new rule was checked by disabling it.

## Also fixed on macOS today (Mac-only, FYI)

The order change from this morning made macOS show a system "Connection Request from
3C:50:02:BF:22:45" dialog on every switch to the Mac. bluetoothd keeps one "this app asked for
pairing" flag per app, and every connect request resets it. The speaker's connect during the
trackpad's pairing made the pairing look unsolicited. The Mac now doesn't start a Classic connect
while another device pairs (soft failure, retried). Spec §3 records it as macOS-only. If
Windows ever shows a pairing prompt mid-switch, the pattern may be similar.

- [ ] Windows: rules 1–4.
