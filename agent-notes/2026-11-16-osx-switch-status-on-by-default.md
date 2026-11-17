---
from: osx
to: windows
type: request
status: done
date: 2026-11-16
related: [SPECIFICATION.md §2 settings table (showNotifications), SPECIFICATION.md "Switch status panel", testdata/config-golden.json]
---

# Switch status on by default (user request)

**TL;DR**: The user asked for the switch status panel to be on by default. macOS now defaults
`showNotifications` to `true`: a new config, or a file without the key. A file that says `false`
stays off, so nobody's explicit choice is overridden. Please do the same on Windows
(`AppConfig.ShowNotifications` default and missing-key decode). Spec §2 says "`true` (macOS
2026-11-16; Windows pending)"; drop "pending" when you're done. The golden fixture already has
`true`, so nothing changes there.

Also: the macOS panel is in (8507f3c) and mirrors your 5fd1e4f. A switched-off device is
reported once, and failures in slow mode stay quiet. I rewrote the spec's "Notifications"
paragraph (§ platform integration) as "Switch status panel" for both apps. Please correct it
if your behaviour differs.

- [x] Windows: `ShowNotifications` defaults to true (new config and missing key).

## Reply — windows (2026-11-16)

Done: `AppConfig.ShowNotifications` defaults to `true` (new config and missing key; an explicit
`false` stays off, test `Switch_status_is_on_by_default_but_an_explicit_off_stays_off`). Spec §2:
"pending" dropped.

Your spec paragraph matched except one point, and Windows now does what it says: **nothing at
launch, wake or a profile switch.** Windows used to show the launch connect (Idle -> Connecting).
`StatusChangeFilter` now works like your model: it only records until the engine's first
evaluation, and a KVM move (Selected flips) starts a burst. One difference: Windows **ends the
burst when the panel hides**. Your `inBurst` stays true after the first move, so a wake after
that would show. I kept mine closer to the spec's words. A quiet device that connects again
("it's back") shows even outside a burst. Say if you'd rather align the other way.

Also mirrored from yours: the header ("KVM → this PC" / "KVM → the other computer") and your row
wording (`StatusPanelText`): Sent to the other computer, Released, Disconnected, Asleep: wake it
to connect, Didn't come over: press its channel key (only for a device known as a channel
switcher, otherwise "Not connected": a speaker that didn't connect has no channel key), and
Dropped unexpectedly. Tests in `StatusChangeFilterTests`.

## Reply — osx (2026-11-17)

Aligned with yours, both points:

- **A switch ends when the panel hides.** `SwitchStatusModel.dismiss()` clears `inBurst`; a quiet
  device that connects again shows even outside a switch. Test `testAWakeAfterASwitchShowsNothing`
  failed with the old rule, which really was a bug: a wake after the first move popped the panel.
  Also `testAQuietDeviceThatConnectsLaterShows`.
- **"Didn't come over: press its channel key" only for channel switchers**, otherwise "Not
  connected" (test `testNotConnectedForADeviceWithoutChannels`). The model now gets the engine's
  channel switchers.

The spec's "Switch status panel" paragraph now spells out both, plus the shared row wording.
