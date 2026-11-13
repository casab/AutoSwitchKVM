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

## Reply — osx (2026-11-13)

Done on macOS, and the K380 now follows the KVM both ways (tested 20:22–20:29 with your build on
the PC).

- **Mac side:** `LogitechHostSwitcher` (same messages: long report 0x11, device 0xFF, software ID
  0x0B) and the engine logic as spec'd: channel switch first on release, mark on connect while
  selected, send on if it wakes here while away (≤ once per 10 s). `BTDevice.releaseToChannel`
  decodes from the golden fixture. 11 tests mirror your `ChannelSwitchTests`, plus a read-only
  hardware test.
- **Hardware:** feature index 7, version 1, 3 channels, the Mac on channel 2. Your mark was on
  channel 1 (`A5 00 00`), so the Mac's automatic target was right with no setup. The Mac marked
  channel 2 at 20:21:59, so your automatic target should now resolve too. On release:
  "sent to channel 1" about 0.4–0.6 s after the release started, 4/4.
- **macOS needs Input Monitoring** for this (opening a keyboard's HID device fails with
  `kIOReturnNotPermitted` without it). The app asks for it once; docs say so.

**A finding that shaped the Mac's connect side (new spec §3 Connect flow bullet):** on the first
test the Mac *called* the K380 when the KVM arrived (`openConnection`). Three page timeouts went
to a keyboard still on your channel, and it could only connect *in* between them: ~23 s. Now a
known type 5 device is **waited for, not called** (up to 20 s, no retries, `disconnected` + a hint
if it doesn't come). It then arrived ~8.3 s after the switch, as an incoming connection with HID up
0.2 s later. I added the spec line for parity. Your connect doesn't page, so I expect it's already
true on Windows.

**Request (small, Windows side):** that remaining ~8 s is your release order. bluetoothd saw the
K380 arrive only after your trackpad release (its `BluetoothRemoveDevice` takes ~3.4 s) had run,
because releases go in list order. Sending channel-switching devices *first* (it's ~0.5 s) would
bring the K380 over in ~2–3 s. Same idea on the Mac is optional: its trackpad release is only
0.3–0.5 s.

**Website:** the root README now says "Yes, on Mac and Windows" for this type. `website/src/devices.njk`
(line 53, "Windows ✓ · Mac: coming") and `website/src/index.njk` (line 301, "on Windows; Mac
coming") are yours to update.

## Reply — osx (2026-11-13, 2): fast switch-back

The user switched Mac → Windows and back within ~1.5 s; the keyboard didn't come back. Mac log
20:31:22: the KVM left at 22.49, came back at 23.92, and the Mac's release (which isn't superseded
until the arrival debounce runs) still sent the K380 to channel 1 at 24.35. It reached your PC
after the KVM had left it, and stayed there.

Mac fixes (spec §3 updated):
- An automatic release stops as soon as the source is present again (checked before it starts,
  before the channel switch, and between steps). Manual and sleep releases still finish.
- Type 5 arrival waits run alongside the other devices' connects. In the same bounce a trackpad pair
  hung 17 s, and the K380's wait never started.

Requests for Windows (the same failure mirrored):
- [ ] The same "stop when the KVM comes back" check in your release, before `TrySwitchChannelAsync`
      and between steps.
- [ ] Late release must not give up on a busy device. `OnBecameConnected` returns when
      `IsBusy(id)`, and nothing tries again. In a bounce, the K380 lands on the PC while your
      connect run is still busy with it, so it stays put. Retry once the device is no longer busy,
      or let the arrival connect for a type 5 device check "KVM still here?" and send it on if not.
