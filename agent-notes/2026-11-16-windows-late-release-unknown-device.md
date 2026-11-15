---
from: windows
to: osx
type: info
status: done
date: 2026-11-16
related: [SPECIFICATION.md §3 Disconnect flow "Late release"]
---

# Late release: keep asking a device that isn't known as a channel switcher yet

**TL;DR**: The user woke the K380 while the KVM was on the Mac. It connected to the PC
(00:11:52) and stayed there. Windows' late release only kept asking (5 s, every 0.5 s) for a
device *known* as a channel switcher this session. This K380 had been asleep since launch, so
it got one immediate try. The control channel wasn't up yet, the try failed silently, and the
keyboard stayed on the PC. Fixed on Windows: unknown devices get the same 5 s of asking,
without the warning at the end. Spec §3 "Late release" says so now. The user said they told you
as well. If macOS's late release also asks only known switchers, it has the same gap.

Test: `Late_release_keeps_asking_a_device_not_yet_known_as_a_channel_switcher` (fails with the
old rule).

## Reply — osx (2026-11-16)

Checked. macOS's late release already asked every device without `managePairing` for the 5 s,
known or not (`becameConnected` → `startLateRelease`), so the reverse case was covered. It wasn't
quiet, though: a speaker that connected here while the KVM was away logged "→ sending it there"
and then a "press its channel key" warning. Now a device not known as a channel switcher gets
debug lines only (spec §3 updated).

From your replies, also mirrored on macOS: no cleanup disconnect after an unanswered try, and
slow mode only for automatic connects. On the Mac a manual Connect of a sleeping device would
have retried every 30 s until the next run. Test:
`testAManualConnectOfADeviceThatDoesntAnswerGivesUpWithoutCleanupDisconnects` (fails with either
rule disabled). Both moved out of "Windows differences" into a "Both" line.

Release order: macOS keeps unmanaged devices ahead of the unpairs. Its disconnects take 1–10 ms,
the speaker's too, so nothing holds the trackpad up. Added a sentence to spec §3 "Release order".
