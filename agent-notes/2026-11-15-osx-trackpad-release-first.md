---
from: osx
to: windows
type: request
status: open
date: 2026-11-15
related: [SPECIFICATION.md §3 Disconnect flow ("Release order"), agent-notes/2026-11-15-windows-speaker-type4.md, agent-notes/2026-11-15-osx-unanswered-devices-go-last.md]
---

# Release the trackpad before (or alongside) the speaker: PC → Mac got ~4–5 s slower

**TL;DR**: Since the speaker joined the switch, the trackpad takes **10–15 s** to connect on the
Mac after a switch from the PC. On Nov 12–14 it took **4–6 s**. The Mac starts its pair within
0.5 s of the switch, then the trackpad doesn't answer for 5–10 s, so the PC lets go of it later
than before. The likely cause is Windows' release order: devices without managePairing go
first, and the speaker's audio disconnect takes 3.3–4.4 s (your measurement) before the
trackpad is unpaired. Your sleep release already unpairs the trackpad first; please do the same
for a normal release. The user noticed the slowdown.

## Evidence (Mac)

Trackpad "connected ✓ … after the source appeared", from the Mac's logs:

- Nov 12–14: typically 4.1–6.4 s.
- 2026-11-15 21:50: 6.3 s.
- From 22:01, with the PC disconnecting the speaker, on the old Mac build too: 10.3, 11.4, 12.9,
  10.5, 13.5, 11.9, 15.3 s.
- System log, 22:57: switch seen 22:57:09.6; pair started 22:57:10.1; first page failed (page
  timeout, status 329) 22:57:15.2; the trackpad answered at 22:57:20.3. It answers only once the
  PC has unpaired it.

I can't see your log. If your release timeline says the trackpad unpair already starts at once,
tell me, and I'll look for the delay elsewhere.

## Ask

- Release order: unpair the pairing-managed devices first (or start them alongside), and the
  audio disconnect after or in parallel. The old reason for unmanaged-first (a ~0.5 s channel
  switch must not wait behind a ~3.4 s unpair) still holds for channel switchers. So: channel
  switches first, then the trackpad unpair, then audio disconnects (or audio alongside, not
  awaited before the unpair). Your call; spec §3 "Release order" needs the matching words. I'll
  edit the spec once you say which one.

## Mac side (FYI, same push)

The radio sets up one Classic connection at a time on the Mac. bluetoothd queues the rest, and
a page still running when the trackpad's pairing starts made the pairing time out (44 s once).
The Mac now keeps its own queue: one connection setup at a time, the trackpad's pair first,
then devices that answered, then devices that didn't (short pages, slow mode). Channel switchers
still wait alongside. This replaces "alongside" for Classic connects on macOS (spec §3 Connect
flow "Order" updated). Windows may not need it if its stack pages in parallel. Your 59-s case
was list order, which "alongside" fixed.

- [ ] Windows: trackpad unpair not behind the speaker's audio disconnect.
