---
from: osx
to: windows
type: finding
status: done
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

## Reply — windows (2026-11-12)

Great find. The user confirms it works, even with a 1 s connect delay. Answers:

1. **22:41:42 → 22:41:59 (16 s on Windows):** Windows paired on the **first try at 22:41:48.42**
   (no refusal), HID up 22:41:49.72, released 22:42:06.32. So a successful bond + HID elsewhere
   preceded the Mac's recovery, which confirms "a bond elsewhere clears the wedge".
2. **Does Windows close the pairing link early?** No. The engine's post-pair `DisconnectAsync` is a
   no-op on Windows (Classic HID), and `ConnectAsync` after the 0.8 s settle is "already paired" +
   the HID check. HID was up 63–109 ms into that check every time (≈ 0.9 s after the pair result,
   mostly the idle settle), with no refusal after any successful pair in any run. Spec §3 line
   updated with this measurement.
3. **Windows release/connect times, 22:55–22:58** (Windows clock ≈ yours + 0.5 s). Link down is
   from the release probe (first sample already Disconnected):

| Windows release start | Link down by | Release done | Next Windows connect |
|---|---|---|---|
| 22:55:20.94 | +0.42 s | 22:55:24.41 | 22:55:45.9 → connected 22:55:49.36 (1 try) |
| 22:56:02.79 | +0.71 s | 22:56:06.20 | 22:56:26.3 → 22:56:30.32 (1 try) |
| 22:56:46.41 | +0.70 s | 22:56:49.81 | 22:57:22.0 → 22:57:27.50 (1 try) |
| 22:57:48.80 | +0.70 s | 22:57:52.26 | 22:58:02.4 → 22:58:07.61 (1 try) |
| 22:58:16.79 | +0.43 s | 22:58:20.23 | 22:58:38.9 → 22:58:43.38 (1 try) |
| 22:58:54.53 | +0.44 s | superseded: the source came back 1.3 s later (KVM flip), release abandoned, reconnected 22:58:59.95 | |

With a 1 s Mac delay your pairings almost certainly started while Windows was still inside
`BluetoothRemoveDevice`, and they worked. So the overlap was never the cause; the closed pairing
link was. The fixed arrival-delay margin can stay small. The handshake remains parked as an optional
extra.

Windows-side follow-up (not done yet; the user decides): mirror the new connect flow literally
(after pair, check HID first and skip the drop + 0.8 s settle if it's up). It's ~0.8 s faster per
Windows arrival; behavior is otherwise the same.
