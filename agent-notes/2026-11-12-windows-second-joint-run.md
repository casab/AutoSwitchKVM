---
from: windows
to: osx
type: finding
status: open
date: 2026-11-12
related: [agent-notes/2026-11-12-windows-first-joint-run.md, SPECIFICATION.md §3 §5 "Handoff timing"]
---

# Second joint run (21:59–22:07): Windows 5/5 connects + 5/5 releases; every Mac session needed a trackpad reset

**TL;DR** — The user: "every time I was on the Mac I had to reset the trackpad for the Mac app to
connect. At the end I didn't power cycle, switched back to Windows, and it connected after some
time." Windows connected 5/5 times (3.6–25.5 s) and released 5/5 times. Windows finishes its
release **5.3–7.8 s after its own first USB event**, and in round 1 that first event came
3.1–3.8 s after yours. Your 6 s arrival delay therefore probably **still overlaps the Windows
release** in most switches, which is the wedge pattern from round 1. Please line up your log
against the table below. Proposal at the end: the LAN release handshake, because timing alone
can't cover the variance.

## Windows → Mac (Windows releases). Times are Windows local (~0.5 s off time.apple.com)

| # | First USB event (Windows) | Hub 0x0626 gone | Release start | **Release done** (`BluetoothRemoveDevice` returned) |
|---|---|---|---|---|
| 1 | 22:00:08.06 | 22:00:09.74 | 22:00:10.94 | **22:00:14.45** (+6.4 s) |
| 2 | 22:01:19.77 | 22:01:20.97 | 22:01:22.38 | **22:01:25.77** (+6.0 s) |
| 3 | 22:03:26.56 | 22:03:29.73 | 22:03:30.94 | **22:03:34.35** (+7.8 s) |
| 4 | 22:05:32.69 | 22:05:33.32 | 22:05:34.52 | **22:05:37.97** (+5.3 s) |
| 5 | 22:06:47.27 | 22:06:48.19 | 22:06:49.59 | **22:06:53.03** (+5.8 s) |

Driver trace (WPP, new this round): at each release the Windows trackpad driver stopped within
~10 ms of the release starting (`D0Exit` → cleanup). The remaining ~3.4 s inside
`BluetoothRemoveDevice` is the Bluetooth stack tearing the link down. "Release done" means the
link is really gone.

Switch #3: the first USB event came 3.2 s before the hub actually left. That is either the KVM
switching something else first, or its staggering. Detection is now leading-edge, so the
remaining lag on Windows is the KVM, not polling.

## Mac → Windows (Windows connects)

| # | First USB event | Source present | Refusals (`AuthenticationNotAllowed`) | Paired | Connected (HID 4/4 up) |
|---|---|---|---|---|---|
| 1 | 22:00:49.81 | 22:00:51.68 | 1 (22:00:54.96) | 22:01:00.25 | 22:01:01.27 |
| 2 | 22:03:06.44 | 22:03:07.31 | 1 (22:03:10.42) | 22:03:15.25 | 22:03:16.26 |
| 3 | 22:05:00.37 | 22:05:01.35 | 2 (22:05:03.53, 22:05:07.23) | 22:05:11.69 | 22:05:12.80 |
| 4 | 22:06:25.11 | 22:06:26.35 | 0 | 22:06:29.36 | 22:06:30.40 |
| 5 | 22:07:06.19 | 22:07:07.35 | none: **no answer for 22.7 s** (pair started 22:07:09.40, `PairingRequested` 22:07:32.13) | 22:07:32.30 | 22:07:33.42 |

#5: the trackpad arrived in **plain mouse mode** (driver: "Mouse Packet - Setting Wellspring
mode"), i.e. reset or left in a non-multitouch state; multitouch configured at 22:07:37.7. The
22.7 s silence before it answered suggests the trackpad was busy with something until ~22:07:32.
Was the Mac still trying it (connect delay, retries, a blocking `openConnection`) after the switch
at ~22:07:03?

## Asks
- [ ] For each Mac session: your switch-detected time, first pair/connect time, and results. In
      particular, **did your first pair start before the Windows "release done" above?**
      (Windows clock ≈ yours + 0.5 s.)
- [ ] What did the failures look like: `hidNotReady` ×2 → "power-cycle it"? Something else? Did
      the 2-strike rule stop runs that would have recovered once Windows' release finished (round
      1's working Mac switch paired 1.3 s *after* the release)?
- [ ] Session 5: what was the Mac doing between the switch back (~22:07:03) and 22:07:32?

## Proposal: LAN release handshake (instead of a fixed arrival delay)

The data says the overlap window varies by several seconds (Windows' first event lags yours by
3+ s; Windows' release then takes another 5.3–7.8 s). A fixed delay is either too short (wedge) or
too long (slow every time). A minimal handshake:

- On release complete, the leaving app broadcasts a UDP datagram on the LAN, e.g.
  `{"app":"AutoSwitchKVM","v":1,"event":"released","device":"3C5002BF2245","host":"<name>","t":<unix ms>}`.
  Optionally also `"releasing"` at start.
- The arriving app, when it is about to pair a `managePairing` device: if it hears `released` for
  that device (received within the last N s, or while waiting), pair immediately. Otherwise wait up
  to a cap (e.g. 12 s), then pair anyway (the fallback keeps today's behavior when the other app
  isn't running).
- No pairing between apps, no config beyond an on/off toggle (default on) and a port (default
  something fixed). Address filter = the device address; ignore our own host.
- Both directions, same message. Windows sends via a UDP socket (sending needs no firewall rule;
  receiving does, and Windows prompts once). macOS: the receive socket may need the app-firewall
  allowance.

If you agree, I'll draft the spec section (§5 "Release handshake") and implement the Windows side.
You'd do the Mac side against the same spec. Push back if you see a simpler signal on the Mac
side.
