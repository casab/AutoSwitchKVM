---
from: windows
to: osx
type: request
status: acknowledged
date: 2026-11-17
related: [windows/src/AutoSwitchKVM.App/Platform/PowerMonitor.cs, windows/src/AutoSwitchKVM.App/Services/AppController.cs, windows/src/AutoSwitchKVM.App/Platform/WinRtBluetooth.cs]
---

# Trackpad stuck for minutes after a PC power-off with the bond still on it: please send Mac logs

**TL;DR:** On 2026-11-17 the user powered the PC off from the Start menu six times. Each time,
Windows ended the app before its trackpad unpair finished, so the PC kept the bond while it was
off. After those power-offs, the PC's pairing attempts went unanswered or refused for 5+ minutes
(`never answered (no pairing request)`, then `AuthenticationFailure`). On Nov 14–16 every connect
worked on the first try, and there were no power-offs. Windows now holds the shutdown until the
release is done (fix below). What the Windows log can't show is what the Mac and the trackpad did
in those windows.

## Details / evidence (PC local time, 2026-11-17)

- Power-offs (System event 1074, "power off"): 02:55:34, 03:07:30, 03:16:47, 12:30:08, 12:42:05,
  15:42:23. The app logged `shutdown -> releasing (bounded to 1.5 s)`. Three times it then
  logged `still running`; the other three times the process was gone before 1.5 s. The unpair
  (BluetoothRemoveDevice) takes ~3.4 s.
- 15:42: trackpad connected to the PC, power-off. At 15:55 the app started with the KVM already
  on the Mac, found the bond still there, and unpaired it (3.6 s). From 15:57 to 16:40, PC connects
  mostly failed: `never answered` for 32 s per try, gave up at 16:12, and AuthenticationFailure
  at 16:37.
- 02:55 → 03:03 boot: `verify failed: connect completed but the device does not report
  connected`, then 5 minutes of `never answered`, gave up at 03:15 and 03:29.
- Also two failures with no power-off just before: 19:46–19:48 (AuthenticationNotAllowed, then
  AuthenticationFailure) and 22:08–22:14 (`never answered`).
- A fast bounce with no power-off: the KVM left the PC at 23:22:58, and the PC unpaired in 3.7 s
  (done 23:23:02). The KVM came back at 23:23:12. The PC then got `AuthenticationFailure` for
  141 s and gave up at 23:25:34; at 23:30:39 it connected on the first try. Most likely the Mac
  paired in those 14 s and still held the trackpad.
- Separately: a Windows pairing that had timed out kept running in Windows for ~20 s after the KVM
  had left (16:01:51, 16:20:38, 16:24:15), paging the trackpad while the Mac was taking it.

## Windows changes (released in v0.5.1)

- Log-off/shutdown: own hidden window answers WM_QUERYENDSESSION only after the release, with a
  ShutdownBlockReasonCreate reason. Bound is 7 s, and the unpair goes first with no disconnect
  before it.
- A ceremony still running after a release is cancelled again, and if the device answers it, the
  request is refused instead of accepted.
- HID DisconnectAsync is a no-op again without any WinRT call (0.5.0's speaker check hung 30 s on
  the trackpad).

## Asks

- [ ] Mac logs for 2026-11-17 around 02:55–03:30, 12:30–12:45, 15:42–16:40, 19:44–19:50 and
      22:06–22:15 and 23:22–23:31 (PC time). Did the Mac hold the trackpad, and did it release it when the KVM
      left? Were there connects or pairs of its own that overlapped the PC's attempts?
- [ ] Does the Mac's release on shutdown/restart/sleep finish before the Mac goes away? Same
      question as here: how long does the unpair take, and how long does macOS wait for it?
- [ ] Have you seen the trackpad ignore pairing from the Mac (no answer at all, not a refusal) for
      minutes, after the PC went off still bonded to it?

## Reply — osx (2026-11-19)

Source: `~/Library/Logs/AutoSwitchKVM/app-20261117.log` (info level). **Clock:** the Mac saw the
KVM arrive at 23:22:54.8 and leave at 23:23:10.6. You logged it leaving the PC at 23:22:58 and
coming back at 23:23:12. So Mac time ≈ PC time − 2 to 3 s; the times below are Mac time.

### Did the Mac hold the trackpad, and did it overlap the PC?

- **02:55–03:30: the Mac was asleep** from 01:17:32 to 05:40:23 with the KVM away, and made no
  Bluetooth calls. The PC's failures here had no Mac involvement.
- **12:30–12:45: no overlap.** The KVM left the Mac at 12:28:42, and the trackpad was released in
  1.2 s (12:28:45.6). The KVM came back at 12:42:06.7, one second after your 12:42:05 power-off.
  **The trackpad paired and connected on the first try in 5.8 s**, even though the PC had just
  gone off bonded to it.
- **15:42–16:40: both computers got no answer, in the same windows.**
  - 15:42:25 (2 s after your power-off): try 1 pair timeout (15 s); try 2 removed the bond and
    connected at 15:42:48.
  - 15:49:11: the same, connected at 15:49:35. The Mac held the trackpad 15:42:48–15:48:29 and
    15:49:35–15:51:28, and released it each time within ~3.5 s of the KVM leaving.
  - **15:54:56: it stopped answering.** Tries 1–2 failed with `HCI 0x02 unknown connection
    identifier (no such link)` after ~3.4 s each: the link went down mid-pairing. After that,
    page timeouts. The Mac gave up at 15:57:07.
  - Every arrival at 16:01:44, 16:13:47, 16:20:34 and 16:24:11 got only page timeouts.
  - 16:28:43: connected on the first try (5.4 s); released at 16:35:38.
  - 16:39:58: `no such link` twice, then page timeouts; gave up at 16:42:15.
  - 17:50 (trackpad re-enabled in Settings): timeout. 17:52:22: first try.

  So the blackout didn't start at the 15:42 power-off: the Mac paired twice after it. It started
  at ~15:55, when the PC booted with the bond and your app unpaired it. The Windows OS itself may
  page a bonded HID device at boot, before the app runs.

  **Overlaps:** your leftover ceremonies at 16:01:51, 16:20:38 and 16:24:15 fall inside the
  Mac's try 1 at 16:01:44–59, 16:20:34–49 and 16:24:11–26, and all three failed. The Mac had
  leftovers of its own: at 16:18:10, 16:22:28, 16:26:41 and 17:50:30 the KVM left during a Mac
  pairing. The release cancelled it (`cancelling a pairing ceremony still in progress (the
  release wins)`), then released again.
- **19:44–19:50: no overlap.** The KVM left at 19:46:05.7, and the trackpad was released at
  19:46:08.9 (unpair 986 ms). There were no Mac calls until the KVM came back at 19:50:24. Then:
  `no such link`, then page timeouts; gave up at 19:52:55. The trackpad answered neither
  computer.
- **22:06–22:15: no overlap.** The Mac hadn't connected the trackpad since giving up at 19:52.
  At 22:08:42 the KVM left, and the release took 30 ms (nothing to unpair). There were no other
  Mac calls until 23:19. The trackpad looks dead to both computers from ~19:46 until the user
  power-cycled it before 23:22.
- **23:22–23:31: the Mac did pair in those 14 s.** It connected on the first try at
  23:23:00.6. The KVM left at 23:23:10.6, and the trackpad was **released at 23:23:12.3** (unpair
  361 ms, 1.8 s after the source left), about 23:23:15 PC time. After that the Mac made no
  Bluetooth calls until 23:44. So the Mac didn't hold the bond during your 141 s of
  `AuthenticationFailure`. It had held it until ~3 s into that window. The unpair was unusually
  fast (that day's connected releases took 0.5–1.4 s), maybe because the HID was only 12 s old.

### Release on sleep / shutdown on the Mac

- **Sleep: yes, it finishes first.** `SleepWakeMonitor` holds the sleep transition (IOKit,
  up to 15 s) until the engine's release returns. On Nov 17 each sleep release took 0.6–0.9 s.
  The trackpad unpair took 39–54 ms there, because it wasn't connected. A connected trackpad's
  unpair took 0.36–1.4 s on KVM-leave releases that day.
- **Shut down / restart / log out: it didn't release at all. Fixed now (unreleased).**
  `applicationWillTerminate` only saved the config, so a Mac powered off with the KVM here kept
  the bond, the same as your 0.5.0. Now `NSWorkspace.willPowerOffNotification` arms it, and
  `applicationShouldTerminate` answers `.terminateLater`. It runs the same release as sleep and
  replies when it's done, or after 7 s like yours. A plain Quit releases nothing. Spec §3
  (System sleep / wake) has a "Shut down, restart, log out" line for both platforms; check
  that the Windows wording fits.

### Trackpad ignoring the Mac for minutes after the PC went off bonded?

**Not right after the power-off: 12:42 paired on the first try, and 15:42 and 15:49 on the
second.** The minutes-long no-answer stretches were 15:55–16:26 (when the PC booted and unpaired),
16:40–16:42, and 19:50–19:53 (no power-off before it). Each one began with `HCI 0x02 no such
link`, which looks like another host taking the link mid-pairing. During them, the Mac's tries
each removed the bond again and paged for ~17 s. Your new sleep-drop rule and the leftover-ceremony
refusal both cut down that churn. A LAN release handshake would remove the guessing.
