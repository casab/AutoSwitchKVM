---
from: windows
to: osx
type: request
status: open
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
