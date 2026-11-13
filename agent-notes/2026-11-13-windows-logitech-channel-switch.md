---
from: windows
to: osx
type: request
status: open
date: 2026-11-13
related: [SPECIFICATION.md §2 BTDevice, §3 Disconnect flow, §5a type 5, testdata/config-golden.json, windows/src/AutoSwitchKVM.App/Platform/LogitechHostSwitcher.cs]
---

# New handoff type 5: Logitech Easy-Switch devices can be switched by the connected host (new config key `releaseToChannel`)

**TL;DR**: Logitech multi-host devices accept a HID++ 2.0 command, feature **0x1814 "Change
Host"**, that tells them to switch to another channel. Only the host they are connected to can
send it, which suits a release. Windows now does this on release instead of unpairing. The
keyboard only goes **both ways** if the Mac does the same when the KVM leaves the Mac. Please
implement the Mac side. Golden fixture change: the device in `testdata/config-golden.json` gained
`"releaseToChannel": null`. Your decoder should accept it (it's per-device, not top-level, so the
key-parity test is unaffected).

What the user wants: their **Logitech K380** (Classic Bluetooth; PC on channel 1, Mac on channel 2)
should follow the KVM.

Protocol (spec §5a and the x1814 PDF in the user's `reference_flow/references/`, gitignored):
- Endpoint: the device's vendor HID collection (usage page `0xFF00`, usage `0x02`), 20-byte long
  report `0x11`. For a direct Bluetooth connection the device index is `0xFF`.
- Root (feature 0x0000) `getFeature(0x18, 0x14)` returns the feature index (K380: 7, version 1).
- `[0] getHostInfo` → `nbHost`, `currHost` (0-based). K380: 3 hosts.
- `[1] setCurrentHost(n)`: no reply; the device leaves.
- `[2] getCookies` / `[3] setCookie(host, byte)`: one byte per channel, stored on the device and
  cleared by the firmware when a new host pairs that channel. **Each AutoSwitch writes `0xA5` on its
  own channel** when the device connects while selected. The leaving host then sends the device to
  the single other channel marked `0xA5`, or to `releaseToChannel` (1-based) if the user set it.
  That way the user configures nothing when both apps run.
- Request format: `[0x11, 0xFF, featureIndex, (function << 4) | swId, args…]`; a reply echoes the
  first 4 bytes. Windows uses swId `0x0B`.

Engine behavior (spec §3 Disconnect flow; Windows: `SelectionEngine.TrySwitchChannelAsync`, tests
`ChannelSwitchTests`):
1. On release, if `managePairing` is off and the device answers 0x1814: switch it to the target
   channel and stop (no disconnect, no unpair). If the target is unknown or the switch fails, warn
   and fall through to the usual release.
2. When it becomes connected while selected: mark our channel (once per session).
3. When it becomes connected while switched away (it was asleep at the switch and woke up on our
   channel): send it on, at most once per 10 s.

On macOS this would be IOHIDManager matching vendor 0x046D, usage page 0xFF00, usage 0x02 (the
device's own HID interface over Bluetooth), then `IOHIDDeviceSetReport` with the 20-byte report
and an input-report callback for the replies. Solaar (Linux) and the reference tool are good
cross-checks.

UI on Windows: type 5 is detected live (ask the device while it's connected). It shows as
"Switches channel on request", and the device row gets an **Other computer's channel**
choice (Automatic / 1 / 2 / 3). Docs: root README, `windows/README.md` and the website `/devices/`
say "Windows ✓, Mac coming". Please update those when the Mac side lands.
