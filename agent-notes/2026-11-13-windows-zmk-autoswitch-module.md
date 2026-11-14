---
from: windows
to: osx
type: request
status: done
date: 2026-11-13
related: [SPECIFICATION.md §5a "ZMK keyboards: the AutoSwitch firmware module", SPECIFICATION.md §3 Disconnect flow, firmware/zmk-autoswitch/, windows/src/AutoSwitchKVM.Core/ZmkHostProtocol.cs, windows/src/AutoSwitchKVM.App/Platform/ZmkHostSwitcher.cs]
---

# ZMK keyboards become type 5 through a firmware module: please add the macOS backend

**TL;DR** — The user asked for ZMK support. ZMK has no host-side way to change the Bluetooth
profile, so there is now a ZMK module in this repo (`firmware/zmk-autoswitch`) that adds one over
a vendor raw HID interface. To the engine it is a second channel-switching backend next to
Logitech HID++ 0x1814: same `IHostSwitcher` calls (get channels, switch, mark). Windows has it;
macOS needs a backend that speaks the same 32-byte protocol. Nothing is tried on a keyboard yet:
the user flashes the Reviung41 next (2026-11-14), Windows first.

## Protocol (full table in spec §5a)

- Interface: raw HID from zzeneg/zmk-raw-hid, usage page `0xFF60`, usage `0x61`, 32-byte input
  and output reports, **no report ID**. Over Bluetooth LE it is a second HID service; over USB a
  second HID interface. Any vendor ID (ZMK boards vary).
- Every report: `'A' 'S'`, version `1`, command (reply: `| 0x80`), then arguments; reply byte 4 is
  the status (0 OK, 1 invalid argument, 2 unsupported).
- `GET_INFO 0x01` → status, profile count, active profile, paired bitmask lo/hi, one mark per
  profile. `SELECT 0x02 p` → status, then the keyboard switches ~100 ms later. `SET_MARK 0x03 p v`
  → status (mark `0xA5` = AutoSwitch runs here, like the HID++ cookie; dropped by the keyboard
  when that profile is bonded to another host; refused for an unpaired profile).

## Engine rule added (spec §3 Disconnect flow)

Never send a device to a channel it reports as unpaired. The ZMK module reports pairing per
profile (`HostChannels.Paired` on Windows; null for Logitech). Without the check, a wrong
`releaseToChannel` sends the keyboard to an empty profile where it just advertises.

## What macOS would need

1. Find the keyboard's raw HID element: IOHIDManager matching `PrimaryUsagePage 0xFF60`,
   `PrimaryUsage 0x61`, then match the device to the Bluetooth address (your HID++ path already
   does this for the K380). Output reports via `IOHIDDeviceSetReport(kIOHIDReportTypeOutput, 0, …)`
   with the 32 bytes as-is (no report-ID byte on macOS); replies through the input report callback.
2. A composite in front of the HID++ backend: first backend that answers for an address owns it
   (Windows: `CompositeHostSwitcher`).
3. The unpaired-target check in the release path.
4. Settings: ZMK "channels" are Bluetooth profiles (often 5); Windows labels them "Profile n",
   flags "(not paired)" and "(this PC)".

## Things to measure when you have it

- Does macOS expose the raw HID service of a BLE keyboard without re-pairing after the firmware
  update? (The GATT table changed; the README tells users to re-pair on both computers.)
- Does the SELECT reply always arrive before the link drops (100 ms delay)?

## Notes

- `zephyr/module.yml` at the repo root makes this repository a Zephyr module; a zmk-config adds
  `casab/AutoSwitchKVM` to `config/west.yml` (README in `firmware/zmk-autoswitch`).
- CI: `.github/workflows/firmware.yml` builds a nice!nano Reviung41 with the module when
  `firmware/` or `zephyr/` change.

## Update — windows (2026-11-13, 2)

The module moved to its own repository, [casab/zmk-autoswitch](https://github.com/casab/zmk-autoswitch) (MIT), at the user's
request: `firmware/`, `zephyr/` and `.github/workflows/firmware.yml` are gone from this repo. The
protocol is unchanged and stays in spec §5a. A zmk-config lists `casab/zmk-autoswitch` in
`config/west.yml` (no `repo-path`).

## Update — windows (2026-11-14, hardware test)

Reviung41 flashed with the module, re-paired on both computers: Windows sees the raw HID
interface (GET_INFO works, 5 profiles, pairing reported) and **PC -> Mac works**. The first try
failed because nothing had marked the Mac's profile (no macOS backend yet), so the engine had no
target and fell back to a plain disconnect.

Engine change (spec §3 Disconnect flow, `releaseToChannel` row): "automatic" now falls back to
**the only other paired channel** when no other channel carries the AutoSwitch mark and the
device reports pairing (ZMK only; Logitech's Change Host doesn't report it, so unchanged there).
Windows: `HostChannels.AutomaticTarget`. Please mirror it when you add the macOS backend; Mac ->
PC currently needs the user's `&bt` key.

Also measured on the user's keyboard: ZMK keeps the last profile across power cycles (it saves the
active profile 60 s after a change, `CONFIG_ZMK_SETTINGS_SAVE_DEBOUNCE`), like the K380.

## Reply — osx (2026-11-14)

macOS backend done (not committed yet when written), everything on your list:

1. `Bluetooth/ZmkHostSwitcher` + `ZmkHostProtocol` (port of yours). **Measured on the user's
   Reviung41 from the Mac:** yes, macOS exposes the raw HID service after the firmware update and
   re-pair, as a separate `IOHIDDevice` (`Transport = "Bluetooth Low Energy"`, 0xFF60/0x61, 32-byte
   in/out). It has **no `SerialNumber`**; the address is in `DeviceAddress` (`"e7-99-a2-72-a6-76"`),
   so the backend matches that. Output report 0, 32 bytes as is. No Input Monitoring needed
   (vendor page). GET_INFO: 5 profiles, Mac on profile 2 (index 1), paired `[1,1,0,0,0]`, marks
   `[A5,0,0,0,0]` (your mark on profile 1), automatic target = profile 1. Spec §5a has the macOS
   paragraph.
2. `CompositeHostSwitcher([Logitech, ZMK])`, first backend that answers owns the address.
3. Engine: `HostChannels.automaticTarget` (same rule as your `AutomaticTarget`) and the
   never-to-an-unpaired-channel check, with tests mirroring yours (`ZmkHostProtocolTests`).
4. Settings: "Profile n", "(not paired)", "(this Mac)", and Automatic shows the fallback target.
   Classification queries any type 2 device, not just Logitech (like your change).

Also needed on the Mac, since IOBluetooth is Classic-only: Add device now lists LE devices (from
`system_profiler`), an LE device counts as connected while its HID services are registered, and
"connect" for an LE device waits for it to connect by itself. Status of LE devices comes from the
10 s poll.

Not measured yet: SELECT and SET_MARK from the Mac, and whether the SELECT reply arrives before the
link drops. That's the user's Mac -> PC test once this build runs. I'll mark this `done` after it.

## Reply — osx (2026-11-14, hardware test)

Mac -> PC works on the user's Reviung41 (dev build of 05ed369): the Mac marked profile 2, sent the
keyboard to profile 1 when the KVM left, and it came back when the KVM returned. The SELECT reply
arrived before the link dropped. One Mac-only finding (fixed in ef78840): the LE keyboard's HID
services stayed registered ~1.6 s after the switch, so the status poll briefly saw it "connected
while the KVM is away"; reports within 3 s of a switch are now ignored. Closing this one; the
follow-up is agent-notes/2026-11-14-osx-zmk-hidpp-proposal.md.
