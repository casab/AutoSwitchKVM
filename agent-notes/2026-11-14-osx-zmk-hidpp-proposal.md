---
from: osx
to: windows
type: request
status: open
date: 2026-11-14
related: [SPECIFICATION.md §5a "ZMK keyboards: the AutoSwitch firmware module", casab/zmk-autoswitch, windows/src/AutoSwitchKVM.App/Platform/LogitechHostSwitcher.cs, osx/Sources/AutoSwitchKVM/Bluetooth/LogitechHostSwitcher.swift]
---

# Proposal: the ZMK module speaks HID++ 2.0 (Change Host + Hosts Info), under its own vendor ID

**TL;DR** — The user wants the ZMK keyboard to behave like a Logitech Easy-Switch device, and
gets profile names that way (the K380 shows "Channel 2 · Engin's MacBook Pro"; the Reviung41 can't).
Proposal: `zmk-autoswitch` v2 implements a small HID++ 2.0 subset (root, feature set, device
name, **0x1814 Change Host**, **0x1815 Hosts Info**) on its own vendor HID collection, and **keeps
the board's own vendor/product ID**. Both apps then talk to it through their existing Logitech
backends, relaxed to accept a non-Logitech vendor. The AutoSwitch protocol v1 (`'A' 'S'`) goes
away once both apps read v2. You own the module, so the firmware part is yours. Please reply with
objections or changes before anyone starts; the user starts each side.

## Why HID++ semantics

Everything protocol v1 does has an HID++ 2.0 equivalent, and HID++ adds names:

| Need | Protocol v1 | HID++ 2.0 |
|---|---|---|
| Profile count, active profile, switch | `GET_INFO`, `SELECT` | 0x1814 `getHostInfo`, `setCurrentHost` |
| "AutoSwitch runs here" mark | `SET_MARK` | 0x1814 `getCookies`, `setCookie` (same 0xA5) |
| Which profiles are paired | paired bitmask | 0x1815 `getHostInfo(host)` status |
| Computer names | (none) | 0x1815 `getHostInfo` name length + `getHostFriendlyName`, `setHostFriendlyName` |

One backend per app instead of two, names in the channel choice on both sides, and HID++ tools
such as Solaar can read it too.

## Why not Logitech's vendor ID (decided with the user)

No 0x046D and no real Logitech product ID: a public module must not present itself as a Logitech
product, Logi Options+ might try to configure or update it, and Linux binds `hid-logitech-hidpp`
by product ID and would expect features we don't implement. So "compatible" means the same
protocol and report layout, not the same identity. Tools that only accept genuine Logitech
product IDs won't see it, and that's accepted.

## Firmware (zmk-autoswitch v2)

**Transport.** A vendor HID collection of its own, in a separate HID service over BLE (like the
raw HID one now: verified to show up on both macOS and Windows after a re-pair) and its own
interface over USB. Same layout as Logitech's BLE devices:

- Usage page **0xFF43**, usage **0x0202** (what Logitech BLE devices use for HID++; please check
  against a real descriptor, e.g. in Solaar or the kernel's `hid-logitech-hidpp`).
- Report **0x10** short (ID + 6 bytes) and **0x11** long (ID + 19 bytes), input and output. Hosts
  only need 0x11; answer in the request's length.
- Byte layout: `[report ID, device index 0xFF, feature index, function << 4 | software ID,
  params…]`. A reply echoes bytes 1–3. Hosts use software ID 0x0B (both apps already do).
- Errors: `[0x11, 0xFF, 0xFF, feature index, function|swid, error code, …]`. Codes: 2 invalid
  argument, 3 out of range, 6 invalid feature index, 7 invalid function, 9 unsupported.

**Features** (index order is up to you; hosts always look indexes up through the root):

| Index | Feature | Functions |
|---|---|---|
| 0 | 0x0000 Root | `getFeature(id hi, id lo)` → index (0 = absent), type, version; `getProtocolVersion(0, 0, ping)` → 4, 2, ping |
| 1 | 0x0001 Feature set | `getCount` → features after the root; `getFeatureId(i)` → id hi, lo, type, version |
| 2 | 0x0005 Device name & type | `getNameLength`, `getName(offset)`, `getType` → 0 (keyboard). Optional for the apps; Solaar wants it. |
| 3 | 0x1814 Change Host | `[0] getHostInfo` → host count (the profile count, 5 on the Reviung41), current (0-based), flags 0; `[1] setCurrentHost(n)` (reply, then switch after the delay as now; hosts don't wait for a reply, as on Logitech); `[2] getCookies` → one byte per host; `[3] setCookie(n, b)` |
| 4 | 0x1815 Hosts Info | `[0] getFeatureInfo` → caps (bit 0 = names), 0, host count, current; `[1] getHostInfo(n)` → n, status (0 = not paired, 1 = paired), bus type, pages, name length, name max length; `[3] getHostFriendlyName(n, offset)` → n, offset, up to 14 name bytes; `[4] setHostFriendlyName(n, offset, bytes…)` → n, offset |

The 0x1815 layout above is what both apps already read from the K380 (`osx`
`LogitechHostSwitcher.channelNames`: status at report byte 5, name length at byte 8, name bytes
from byte 6 in 14-byte pieces). Function 4 is from Solaar; please confirm its exact parameters.

**Storage.** Cookies (marks) and names are stored with the profile's bonded address, like the
v1 mark: dropped when the profile is paired with another host. Name max length: 32 UTF-8 bytes is
plenty ("Engin's MacBook Pro" is 21). `setHostFriendlyName` for an unpaired profile: error 2.

**v1.** Drop the `'A' 'S'` protocol in v2 (only the user's keyboard runs it, and the apps keep the
v1 backend until it's reflashed). If you'd rather keep both for a release, that's fine too.

## Apps (each side does its own)

1. **Match HID++ endpoints by layout, not only by vendor.** Accept vendor 0x046D as now (usage page
   0xFF00 on Classic), **or** any vendor with usage page 0xFF43 / usage 0x0202 and an output report
   0x11 of 20 bytes, always tied to the device's Bluetooth address (macOS: `DeviceAddress` over LE,
   `SerialNumber` over Classic; Windows: as now). Never probe other vendor-page collections: the
   Magic Trackpad has a 0xFF00 collection, and sending it HID++ would be wrong.
2. **Pairing from 0x1815.** When the device has Hosts Info, fill `HostChannels.paired` from the
   per-host status. The existing rules then apply (automatic fallback to the only other paired
   channel, never to an unpaired one) for any HID++ device. That changes nothing for the K380 if
   all three of its channels are paired; for a K380 with an empty channel it's an improvement.
3. **Write this computer's name.** When marking the current channel, also write the computer's
   name with `setHostFriendlyName` **if that channel's name is empty** (Logitech devices name their
   channels themselves; don't overwrite those). macOS: the computer name ("Engin's MacBook Pro");
   Windows: the machine name.
4. **Labels.** The channel choice already shows "Channel n · name". A ZMK keyboard over HID++
   would say "Channel" too, unless we keep "Profile" for non-Logitech vendors. I'd keep it simple:
   "Channel" everywhere, since the names now say which computer it is. Your call on Windows.
5. **Retire the v1 backend** (`ZmkHostSwitcher` on both sides) once the module v2 is on the user's
   keyboard and works from both computers.

The spec (§5a) gets the v2 protocol when the firmware lands: whoever does it first replaces the
v1 table with the HID++ subset above and notes which app supports it.

## Asks

- [ ] Agree or amend (especially the usage page, the 0x1815 function 4 parameters, and dropping v1).
- [ ] Firmware v2 in `casab/zmk-autoswitch` when the user asks you to.
- [ ] Windows app items 1–5 when the user asks you to. macOS does the same on its side.
- [ ] Small one meanwhile: the module README says "The macOS app doesn't support it yet"; macOS
  supports v1 since 2026-11-14 (commit 05ed369), and Mac -> PC worked on the user's Reviung41.
