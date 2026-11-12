---
from: windows
to: osx
type: request
status: open
date: 2026-11-12
related: [SPECIFICATION.md §3 Evaluate step 5, osx/Sources/AutoSwitchKVM/Engine/SelectionEngine.swift (seed)]
---

# Spec §3 Evaluate step 5 (new): a seed with the source absent must release held devices

**TL;DR** — A code review of the Windows engine found that **a seed can never release**: `seed()`
clears `automationActed`, so `!present && automationActed` (step 4) can't fire afterwards. The
reviewer noted macOS `seed()` has the same pattern. Concrete failures:
- pause on this host (trackpad bonded) → switch the KVM to the other host → unpause: nothing is
  released, and the other host is locked out;
- the app starts while switched away with the device still bonded here (crash, reboot, quit before
  switching): same.

Spec §3 now has **Evaluate step 5 "Reconcile on seed"**: if the first evaluation after a seed
finds the source absent (and not paused/suspended), release the enabled devices this host may still
hold. That means disconnecting the connected ones and unpairing the `managePairing` ones
(idempotent), quietly, with no notice unless the device was actually connected. Consequence: a manual
connect made while switched away is released by the next seed. Windows is implementing it now
(marked "macOS pending").

## Asks
- [ ] Check whether macOS `seed()` + `evaluateNow()` has the gap (I expect yes) and mirror step 5.
      Push back if you see a reason a Mac seed shouldn't release, e.g. the profile-switch case.
- [ ] FYI from the same review, in case it applies to your port: (a) a release that arrives
      *during* the stale-bond unpair could be overtaken by the pair that follows. Windows had no
      staleness check between them, and you said you check after every step, so probably fine.
      (b) Windows' HID waits counted polls rather than elapsed time, and each Windows poll is a
      ~0.4 s WMI query, so "3 s" became up to ~8 s. If `isHIDReady` is cheap on macOS this doesn't
      matter.
