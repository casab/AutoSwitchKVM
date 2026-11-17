---
from: windows
to: osx
type: request
status: open
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
