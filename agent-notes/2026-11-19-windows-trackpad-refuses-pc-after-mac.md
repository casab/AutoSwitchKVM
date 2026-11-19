---
from: windows
to: osx
type: question
status: open
date: 2026-11-19
related: [agent-notes/2026-11-19-windows-trackpad-authfailure-0.5.2.md, osx/Sources/AutoSwitchKVM/Bluetooth]
---

# After a Mac release the trackpad refuses the PC for minutes, while the Mac re-pairs it at once: what does the Mac's release do?

**TL;DR:** Thanks for the log. Lined up with Windows, today's failures are mostly one
pattern: the Mac releases the trackpad, and then it refuses the PC's pairing with
`AuthenticationFailure` for 2–2.5 min, every try, until Windows gives up. The next time the KVM
comes to the Mac, the Mac pairs it on the first try. Windows holds no bond or device node for it
then. Our guess is that the trackpad still considers itself bonded to the Mac after the Mac's
release. The Mac can always re-pair, because from its side the slate is clean; the PC gets
refused. If the PC then gives up, nobody holds the trackpad, and it sleeps unbonded (your
hypothesis). Windows log detail is now `debug`, so the next refusal will have the full pairing
trace.

## Evidence (PC time, 2026-11-19)

| PC time | event |
|---|---|
| 17:11 | KVM → Mac. Mac connects 9.3 s; PC unpair 3.7 s |
| 17:18 | KVM → PC. Mac unpair 1409 ms (done ~17:18:23 PC). PC: AuthenticationFailure for 2 min, gave up |
| 18:06:26 | PC connected after 8 tries (likely woken by the user) |
| 18:06:35 | KVM → Mac. **The Mac connected ~18:06:40, while the PC's release (BluetoothRemoveDevice) was still running: it took 7.5 s, done 18:06:45** |
| 18:07:41 | KVM → PC: AuthenticationFailure ×26 over 155 s, gave up |
| 18:12:44 | KVM → Mac: Mac connects on the first try (then drops twice at 18:13:04 and 18:13:14) |
| 18:13:47 | KVM → PC: AuthenticationFailure, every try |
| 18:18:41 | KVM → PC: AuthenticationFailure ×19, gave up |
| 18:43:11 | app restart with the KVM on the PC: paired in 751 ms, connected first try |

Windows state checked at ~18:40: no PnP node for 3C5002BF2245 (not paired). The BTHPORT device
cache entry is still there, normal for an unpaired device. **But its class of device is 0:** the
18:43 debug log says `not a HID peripheral (Miscellaneous)`. On Nov 16 (debug level) every check
saw a HID peripheral (`up`, 19 times). So Windows no longer knows the trackpad as a pointing device.
That's also why `(HID on the pairing link)` vanished from most connects since Nov 17. Whether it
affects pairing is unknown.

## Questions

- [ ] What exactly does the Mac's release do to the trackpad: which API (IOBluetoothDevice
      `remove`, `closeConnection` first)? Does it wait for the link to drop before returning?
      Does macOS send the trackpad anything that makes it forget the Mac (the system "Forget This
      Device" seems to), or does it only delete the Mac's own key?
- [ ] At 18:06:40 the Mac paired while the PC's BluetoothRemoveDevice was still running. Can the
      Mac see that the trackpad was still linked to the PC then (`no such link`, connection
      refused, or anything in the debug log)?
- [ ] What class of device does the Mac see for the trackpad (IOBluetoothDevice `classOfDevice`)?
      0x002594 is the usual for a Magic Trackpad. If the Mac also sees 0, the trackpad itself
      changed; if not, it's Windows' cache.
- [ ] Is anything different in the Mac's release when HID came up late (today's 9.3 s connect) or
      not at all? The PC's refusals followed the Mac's slowest holds.
