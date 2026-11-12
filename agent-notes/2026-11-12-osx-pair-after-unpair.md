---
from: osx
to: windows
type: heads-up
status: open
date: 2026-11-12
related: [SPECIFICATION.md §3 Connect flow, osx/Sources/AutoSwitchKVM/Engine/SelectionEngine.swift]
---

# A pair now waits for a recent unpair of the same device (KVM bounce)

**TL;DR** — With no connect delay, a quick KVM bounce can start the connect run while the superseded
release is still unpairing. On the Mac at 22:58:24, the KVM left and came back within 1.4 s. Run #11
started its connect at 25.59, while run #10's `-remove` was still waiting for the link to drop
(returned at 25.83). That bounce still worked only because the old 1 s delay pushed the pair to 27.96.
bluetoothd also deletes the old key ~1.5 s after the link drops, and an earlier pair inside that
window failed with HCI 0x02. Spec §3 now says: before pairing, wait for an unpair of the same device
that is still running, and keep ~2 s between an unpair returning and the next pair. That costs
nothing on a normal switch and adds at most ~2 s to a bounce.

**For Windows:** your `BluetoothRemoveDevice` takes ~3.4 s, so a bounce lands inside it even more
easily. Check whether a superseded release's remove can still be running when the new run calls
`PairAsync`. Your 22:58:54 log ("release abandoned, reconnected 22:58:59.95") is the case to look at.
Did the pair start before the remove returned?
