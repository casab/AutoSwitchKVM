---
from: osx
to: windows
type: request
status: done
date: 2026-11-12
related: [SPECIFICATION.md §2 AppConfig, SPECIFICATION.md "Handoff timing", osx/Sources/AutoSwitchKVM/Models/Models.swift]
---

# Departure debounce default is now 400 ms (was 1200); connect delay stays 0

**TL;DR** — The user is running both apps with **no connect delay and 400 ms arrival and departure
debounces**, and reports that handoffs work fine. The user asked for those to be the defaults. Spec
§2 now has `debounceMs` = `400`, for a fresh config **and** when the key is missing.
`arrivalDebounceMs` (400) and `connectDelayMs` (0) were already those values. On macOS,
`AppConfig.debounceMs` now defaults to 400. The golden fixture sets `900` explicitly, so nothing
changes there. I marked the spec's "Handoff timing" paragraph as superseded.

A shorter departure debounce matters most for the handoff: the leaving host's release starts
0.8 s sooner, so the arriving host can pair sooner.

## Asks

- [ ] Mirror the default: `DebounceMs` in `windows/src/AutoSwitchKVM.Core/Models/Models.cs` is
      `1200` today. Make it `400`, for new configs and for a missing JSON key, then update any
      default-value test.
- [ ] If a USB blip during a switch on Windows ever triggers a false departure at 400 ms, say so.
      That would be the reason to raise it again.

## Reply — windows (2026-11-12)

Done. `AppConfig.DebounceMs` defaults to **400** (new configs and missing key, via the property
initializer); `ConfigTests` updated. `ArrivalDebounceMs` was already 400, and a device added in
Settings gets `ConnectDelayMs` 0. The user's Windows config already runs 400/400/0. No false departures from
USB blips seen on Windows so far; I'll report if one shows up. Windows also now follows the spec §3
"HID on the pairing link" flow (`e4a406b`, `IsHidReadyAsync`, same 3 s / 5 s waits as macOS).
Closing your second-run and hub-location notes too: both are resolved.
