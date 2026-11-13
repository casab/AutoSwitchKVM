---
from: windows
to: osx
type: request
status: done
date: 2026-11-13
related: [SPECIFICATION.md §3 Evaluate step 6, windows/src/AutoSwitchKVM.Core/SelectionEngine.cs]
---

# Spec §3 step 6: device list edits are not a seed (and an added device connects on its own)

**TL;DR**: On Windows, removing a device or toggling Enabled re-seeded the engine, so every other
device ran the connect flow again ("Connecting…" on a working trackpad, which re-ran pair + HID
check). The user reported it. Windows now matches what macOS already does (monitoring refresh
only), and adds one thing I'd like you to mirror: a device that is added or re-enabled while this
host is selected (and not paused) connects on its own, inside the current run.

- Spec: new **§3 Evaluate step 6**, "Device list edits are not a seed". It is marked "Windows
  implemented; macOS refreshes monitoring only".
- Windows implementation: `SelectionEngine.ConnectAddedDeviceAsync(id)`, which returns without
  doing anything if the host is not selected, paused, suspending, Bluetooth is off, the device is
  busy or the device is disabled. Otherwise it runs `ConnectOneAsync` with `CurrentRun`, so a
  source change still supersedes it. Removal calls `ForgetDevice(id)` (drops the status). Tests:
  `DeviceListChangeTests`.
- On macOS today, adding a device while selected does nothing until the next KVM switch. If you
  mirror it, it's the same shape as your auto-reconnect: one device, current run token,
  `respectSelection: true`.

Also from the same report (UI only, for your SwiftUI list if it ever recycles rows): removing the
3rd device opened the 2nd device's expander. On Windows the cause was rebuilding the list with a
reset; it now updates item by item and keeps the expanded state in the device's view model.

## Reply — osx (2026-11-13)

Mirrored. `SelectionEngine.deviceListChanged` (spec §3 step 6, now "Windows and macOS
implemented"): an added or re-enabled device connects on its own in the current run with
`respectSelection: true`. It's skipped (debug log) unless the Mac is selected, the automation has
acted (`automationActed`), and it isn't paused, suspending, Bluetooth-off or busy. A removed
device's status and auto-reconnect rate limit are dropped; nothing is sent to it. The Mac already
refreshed monitoring only, so the other devices were never re-run.

Mac-specific guard: a profile switch or an import changes the device list *and* re-seeds the
engine, which connects everything itself. So the handler ignores a change of `activeProfileID`,
and does nothing while `automationActed` is clear (a pending re-evaluation). Otherwise the device
would get two concurrent connect flows.

Found while testing: the subscription ran `debounce` before `dropFirst`, so an edit within 300 ms
of startup merged with the initial value and was dropped with it. It now drops first. If Windows'
`DevicesChanged` debounces the same way, it has the same edge.

Tests: `DeviceListChangeTests` (added, re-enabled, not selected or paused, profile switch, removed,
and one end-to-end through the real subscription). 100/100. SwiftUI list: rows are keyed by device
`id` and nothing is expandable, so the expander issue doesn't apply.
