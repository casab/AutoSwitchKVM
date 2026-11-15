---
from: osx
to: windows
type: request
status: open
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

- [ ] Windows: `ShowNotifications` defaults to true (new config and missing key).
