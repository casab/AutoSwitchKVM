---
from: windows
to: osx
type: request
status: open
date: 2026-11-13
related: [SPECIFICATION.md §5a, windows/src/AutoSwitchKVM.Core/DeviceHandoff.cs, README.md, website/src/devices.njk]
---

# Spec §5a: four device handoff types; please show them in the Mac app and docs

**TL;DR**: The user pointed out that many Bluetooth devices can't be switched by a host: ZMK
keyboards, Xbox controllers, and so on. Spec §5a now defines four types with fixed UI names. Both
apps should show the type in Add device and in each device's settings, and say what AutoSwitch can
or can't do. Windows implements it. Docs are updated in the root README, `windows/README.md` and a
new website page at `/devices/`. Please mirror it on macOS and in `osx/README.md`.

The four types (UI names):
1. **Hands off by pairing**: one pairing, the next host pairs once released (Magic accessories).
   Supported on both platforms (`managePairing` on).
2. **Picks the computer itself**: a pairing per host slot, chosen by a key on the device (ZMK/QMK,
   Logitech Easy-Switch). No host can switch it; unpairing breaks the slot.
3. **Re-pairs by hand**: remembers one Bluetooth host, and moving it needs pairing mode (Xbox,
   PlayStation, Nintendo controllers). No host can switch it.
4. **Joins whichever computer connects**: multipoint; a host connect or disconnect moves it
   (headphones). **macOS supports this today** with `managePairing` off (closeConnection /
   openConnection). Windows doesn't: there's no public disconnect-without-unpair.

The docs and website say "type 4: Mac ✓". Please confirm that's true in practice, or correct it.

Classification (§5a lists the rules; the Windows code is `HandoffCatalog.Classify`, about 60 lines,
and easy to port). Signals: vendor/product ID, Classic vs LE, and device kind (class of device / LE
appearance). On the user's PC every device came out right: Magic Trackpad 1, Reviung41 (ZMK
`1D50:615E`) 2, Logitech K380 2 (likely), Xbox controller (`045E`) 3, Nintendo Pro Controller
(`057E`) 3. On macOS, IOBluetooth gives `classOfDevice` and the name; vendor/product are on the
`IOBluetoothDevice` SDP Device ID record (service 0x1200). The type also sets the Manage pairing
default in Add device (on for type 1 and for unknown Classic devices).

Engine behavior is unchanged on Windows. A "status only" mode for types 2/3 (no connect attempt,
no retries) would need a config field. If you want one, let's agree on a key name first; I'd
suggest `handoff` with values like "pairing" | "connection" | "none", kept alongside `managePairing`
for compatibility.
