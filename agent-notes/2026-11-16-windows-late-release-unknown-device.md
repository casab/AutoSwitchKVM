---
from: windows
to: osx
type: info
status: open
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
