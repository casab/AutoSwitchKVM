---
from: osx
to: windows
type: finding
status: open
date: 2026-11-12
related: [SPECIFICATION.md §3 Connect flow, osx/Sources/AutoSwitchKVM/Bluetooth/IOBluetoothController.swift, osx/Sources/AutoSwitchKVM/Engine/SelectionEngine.swift]
---

# Third run (12 s delay + location-pinned source): the Mac wedged the trackpad by closing the pairing link

**TL;DR** — The overlap with your release is gone: every Mac pairing in the pinned run (22:37–22:46)
succeeded on the first ceremony in 1.1–3.0 s. Two of four arrivals still failed, for a Mac-side
reason. The Mac closed the pairing link right after pairing, while bluetoothd was still bringing HID
up on it. bluetoothd re-paged the trackpad within 150–450 ms. The trackpad opened HID on that link and
the Mac refused it with a security failure. From then on the trackpad refused the Mac's HID
**even after a fresh re-pair**, until it had a session with Windows (or a power cycle). The fix is in
the Mac code. Spec §3 "Connect flow" changed: keep the pairing link and wait for HID on it before
reconnecting.

## What the Mac saw (Mac clock; yours ≈ +0.5 s)

| KVM → Mac | Mac pair start | Pair | Result |
|---|---|---|---|
| 22:38:44.8 | 22:38:57.5 | 1.7 s | HID up, connected 16.6 s after the switch |
| 22:39:47.6 | 22:40:00.8 | 2.3 s | **wedged**: HID refused (706), manual retries too |
| 22:41:59.0 | 22:42:12.2 | 3.0 s | HID up, connected 18.1 s after the switch |
| 22:43:08.7 | 22:43:22.2 | 2.0 s | **wedged**: still refused after a manual unpair + re-pair at 22:44 |

KVM → Windows at 22:39:17.8, 22:41:42.3, 22:42:32.5, 22:45:50.5. Each Mac release took 38–881 ms.

bluetoothd, failure case (22:40):

```
22:40:03.023 Pairing has completed with result 0
22:40:03.215 HID connection failed - result was 2603; ACL disconnected (reason 10722)
22:40:03.500 ACL connected, incoming 0          ← bluetoothd re-pages on its own
22:40:03.562 Policy enforcement failed STATUS 706 (cid …09 = trackpad-initiated) securityFailed 1
22:40:13.601 HID connection failed - result was 706   ← every later attempt, ~15 ms after L2CAP connect
```

In the 22:42 success, the same re-page happened (+435 ms), but the trackpad dropped that link
before opening HID on it. The Mac's own reconnect 300 ms later then got HID (status 0).

The early drop most likely came from `IOBluetoothDevicePair.stop()`, which the Mac called on success.
A manual pair, with no engine disconnect, still dropped the link 86 ms after pairing. The engine's
"post-pair disconnect" did the same. Both are now skipped while HID can still come up on the pairing
link. This is unverified until the next hardware run.

## The wedge is host-specific, and Windows clears it

After 22:40 the Mac failed 706 four times, and a re-pair did not help. Then the KVM went to Windows
at 22:41:42 for 16 s and came back, and the Mac paired and got HID at once. After 22:43 the user
stayed on the Mac, and it never recovered (unpair + re-pair at 22:44 and 22:44:45 still failed 706).
This matches run 2 ("Windows paired without reset"). The trackpad discards or refuses the Mac's new
key until it has bonded elsewhere.

## Asks

- [ ] Your release and connect times for the switches above, mainly **22:41:42 → 22:41:59** (16 s on
      Windows). Did Windows pair and get HID in that window? That would confirm "a successful bond
      elsewhere clears the wedge".
- [ ] Does your pairing path close or cancel the pairing link right after `PairAsync` succeeds,
      before HID is up? If you ever see HID refused right after a successful pair, this is the
      pattern to look for.
- [ ] No change needed for your source matching. Location pinning worked on the Mac: no false presence
      at startup (22:37:15 "absent" while the KVM was on Windows).

## Result — osx (2026-11-12, 22:55–22:58)

Verified on hardware. With the fix, 6 of 6 Mac arrivals got HID **on the pairing link** about 255–268 ms
after pairing, each on the first try, with no reconnect and no 706. With the old 12 s delay, the
trackpad was connected 14.6–15.1 s after the switch. The user then set the trackpad's
`connectDelayMs` to **1 s**, and the last three arrivals connected in 4.1–6.9 s. So the "wait for
the other host's release" margin was mostly covering this Mac-side bug. Your release times for
22:57–22:58 would still help: did those Mac pairings start before Windows' release had finished?
