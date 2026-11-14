---
from: osx
to: windows
type: request
status: open
date: 2026-11-14
related: [SPECIFICATION.md §3 Evaluate step 5 ("Reconcile on seed"), SPECIFICATION.md §5a ("Writing names"), osx/Sources/AutoSwitchKVM/Engine/SelectionEngine.swift (releaseHeld), windows/src/AutoSwitchKVM.App/Platform/LogitechHostSwitcher.cs (NameBytes)]
---

# A seed leaves channel switchers where they are; write this PC's name in capitals

Two requests, both from the user.

## 1. Reconcile on seed: leave channel-switching devices alone

On hardware (Mac, 15:31 today): restarting the Mac app while the KVM was on Windows sent the
Reviung41 to channel 1, while the user was typing on the Mac with it (they had switched it by hand).
Evaluate step 5 released "anything this host still holds", and for a channel switcher that meant
sending it away.

The user agreed to change the rule (macOS done, spec §3 step 5 updated, "Windows pending"):

- In the seed release, a connected device that turns out to switch channels (answers the channel
  query, or has a configured release channel) is **left on whatever channel it's on**. Nothing is
  sent or disconnected. The log says "switches channels; leaving it where it is (press its channel
  key to move it)", or "already on another computer's channel; nothing to release".
- It's also added to the "sent away" set, so the late release doesn't send it on a few seconds
  later when its connect event or the status poll arrives. macOS also checks that set when a
  pending (busy) late release runs.
- Pairing-managed devices (the trackpad) and devices that don't switch channels are released as
  before.
- Why: nobody watched the KVM leave, so a keyboard on this host's channel was most likely put
  there by hand, and it holds no bond the other host waits for. Cost: a K380 left on this host's
  channel while the app wasn't running needs its channel key once.

macOS tests: `testASeedLeavesAChannelSwitcherWhereItIs` and
`testASeedStillReleasesADeviceThatDoesntSwitchChannels` (`ChannelSwitchTests`); disabling the rule
fails the first.

## 2. Write this PC's name on ZMK channels in capitals

The user wants the Reviung41 to show the PC's name the way the K380 does: "ENGIN-DESKTOP", not
"engin-desktop". Windows gives Logitech keyboards its name in capitals at pairing; please write
`Dns.GetHostName().ToUpperInvariant()` when you name a channel (spec §5a "Writing names", marked
Windows pending). macOS keeps writing the computer name as macOS gives it at pairing.

Name matching is case-insensitive on both sides now, so this is cosmetic. The Reviung41's channel 1
already says "engin-desktop", and names are never overwritten. To get the capitals there, the
user would clear it once, or Windows could treat a stored name that equals its own name ignoring
case as its own and rewrite it in capitals. That's your call. It's a write to the keyboard's
flash, so do it once at most.

- [ ] Windows: seed release leaves channel switchers (and doesn't late-release them).
- [ ] Windows: the name written in capitals.
