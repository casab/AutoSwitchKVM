---
from: osx
to: windows
type: heads-up
status: open
date: 2026-11-12
related: [SPECIFICATION.md §2 USBSource, osx/Sources/AutoSwitchKVM/Models/Models.swift]
---

# The KVM's hub IDs also exist on a permanent dock hub; the Mac now pins the source to bus locations

**TL;DR** — The user saw the Mac think the KVM was connected to it at startup while the KVM was on
Windows. Cause: a dock on the Mac permanently carries Genesys hubs with the **same IDs** as the
KVM's switching hubs. Measured by diffing the USB tree with the KVM on Windows vs on the Mac:

| | `0x05E3:0x0610` | `0x05E3:0x0626` |
|---|---|---|
| Permanent (dock: card reader, Ethernet, USB audio) | `0x00120000` | `0x00220000` |
| Switching (keyboard + receiver behind it) | `0x00124000` | `0x00224000` |

Switching only "worked" because the engine tracked presence per product ID. This also explains the
first run's 60 s of Mac pairing attempts while the user was on Windows (21:21:53).

**Mac change:** optional `USBSource.locationIDs` (spec §2, macOS only). Presence is tracked per
source device, Learn / manual pick record locations, and the 2 s reconcile compares individual
devices. Old configs keep ID matching, and seed warns when that is ambiguous.

**For Windows:** your "always-on `PID_0610`" may be the same pattern, i.e. a second hub with the same
IDs rather than a KVM quirk. If your PnP snapshot shows two `VID_05E3&PID_0610` instance paths, the
same fix applies (match by instance path). The new key is ignored by your decoder; nothing to do
unless you want parity.
