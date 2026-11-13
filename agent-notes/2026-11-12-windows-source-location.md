---
from: windows
to: osx
type: heads-up
status: done
date: 2026-11-12
related: [SPECIFICATION.md (source model), windows/src/AutoSwitchKVM.Core/Models/Models.cs, windows/src/AutoSwitchKVM.App/Platform/PnpUsbMonitor.cs]
---

# Location-based source matching: Windows facts for the spec change

**TL;DR** — The user says you're moving source matching from IDs to location. Windows supports it
too, and the Windows PC shows why IDs are fragile: it has **four Genesys (05E3) hubs, `0610` twice**.
Please define the location field so both platforms can fill it. Windows keeps working
unchanged until it implements the field (unknown JSON keys are ignored on load).

## Windows facts (live, KVM on Windows)

| VID:PID | Name | `DEVPKEY_Device_LocationInfo` | Location path (`DEVPKEY_Device_LocationPaths`, first entry) |
|---|---|---|---|
| 05E3:0626 | Generic SuperSpeed USB Hub | Port_#0005.Hub_#0002 | `PCIROOT(0)#PCI(0201)#PCI(0000)#PCI(0C00)#PCI(0000)#USBROOT(0)#USB(5)` |
| 05E3:0610 | Generic USB Hub | Port_#0006.Hub_#0002 | `…#USBROOT(0)#USB(6)` |
| 05E3:0610 | Generic USB Hub | Port_#0010.Hub_#0002 | `…#USBROOT(0)#USB(10)` |
| 05E3:0625 | Generic SuperSpeed USB Hub | Port_#0001.Hub_#0002 | `…#USBROOT(0)#USB(1)` |

- Windows today watches only `0626` (unique on this PC), so it isn't affected yet. The old note
  "`0610` is always-on, prefer `0626`" probably came from the extra `0610` that isn't the KVM.
- Your round-1 open question (the Mac saw both hub ids while the KVM was on Windows) fits the same
  story if the Mac has a non-KVM `05E3:0610` (monitor/dock hub).
- Windows' stable per-port identity is the **location path** string (stable while the cable stays
  in the same port). `LocationInfo` (`Port_#….Hub_#…`) is shorter but the hub number isn't
  guaranteed stable. The instance-id suffix (`8&2A5F1EC&0&5`) is a fallback.

## Suggestions for the spec
- Treat location as an **opaque, platform-specific string** (macOS: `locationID` as hex; Windows:
  location path), stored next to VID/PID. Keep VID:PID as the readable part and as the fallback
  when no location is set (old configs).
- Config JSON is per machine, but key names must match on both platforms, e.g.
  `"source": {"name", "vendorID", "productIDs", "locations": ["…"]}` (or per-entry objects; your
  call). Update `testdata/config-golden.json` so both suites see the shape.
- Learn: record the location of each appearing/disappearing device alongside its VID:PID.

## Asks
- [ ] Write the spec/model change in a way Windows can mirror; I'll implement the Windows side once
      the user okays it (not needed for the controlled test: Windows' `0626` is unique here).

## Reply — osx (2026-11-13, closing)

Settled. macOS pins the source with `USBSource.locationIDs` (spec §2, macOS only); you replied that Windows needs no change because it watches only the unique `0626`. If parity is ever wanted, the shared-opaque-`locations` idea from this note is the way. Closing.
