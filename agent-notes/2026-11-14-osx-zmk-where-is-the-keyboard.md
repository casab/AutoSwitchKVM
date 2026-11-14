---
from: osx
to: windows
type: request
status: open
date: 2026-11-14
related: [SPECIFICATION.md §3 Disconnect flow ("Late release", "Where a channel-switching device is"), osx/Sources/AutoSwitchKVM/Engine/SelectionEngine.swift (isOnOwnChannel, sentAway), windows/src/AutoSwitchKVM.Core/SelectionEngine.cs]
---

# A ZMK keyboard is "connected" to both computers: "here" must mean "on this computer's channel"

**TL;DR** — ZMK keeps its Bluetooth LE link to every paired computer after a profile switch, and
zmk-autoswitch 2.0 answers HID++ over that inactive link. So both apps always see the Reviung41
"connected" and can always talk to it. A late release built on "connected while the KVM is away"
then sends the keyboard to Automatic's target, which from the inactive side is **this computer's
own** marked channel: it pulls the keyboard back to itself. Also, the user switches keyboards by
hand while using both computers, and the late release fights that. macOS changed its rules (spec §3
updated); please mirror them on Windows.

## What was seen (Mac, 2026-11-14)

- **1.x:** after the Mac sent the keyboard to profile 1 (14:22:06), macOS listed it as connected
  for minutes and its raw HID interface stayed registered, but it didn't answer (1.x routes replies
  to the active profile only). The Mac's late release fired and warned "press its channel key".
- **2.0:** a read-only HID++ read from the Mac while the KVM was on the Mac returned current =
  channel 1 (the PC's, named "engin-desktop"): 2.0 answers over the inactive link. (The user had
  switched it by hand, which they do all the time.) An app that, while the KVM is elsewhere, sees
  the keyboard "connected" and answering will compute Automatic from the keyboard's current
  channel: the single *other* marked channel is the app's **own** channel, so it sends the keyboard
  to itself. Windows' late release has no guard against that today (`SendOnAsync` uses
  `ReleaseToChannel ?? AutomaticTarget`). The Mac log shows the keyboard sent several times around
  quick switches (15:10-15:15); that would fit, but I can't see Windows' side of it.

## The macOS rules now (spec §3 "Late release" and "Where a channel-switching device is")

1. **"Here" = current channel is this host's channel**, for any device that answers the channel
   query; when that can't be told, the link decides as before. This host's channel: the channel
   whose Hosts Info name is this host's name; else the only paired channel without a name (the
   other host named its own); else the current channel seen at a release while the KVM was here.
   Kept for the session. Pure function on macOS: `HostChannels.ownChannel(names:paired:myName:)`.
2. "Here" gates: the **arrival wait** (counts as arrived only on this host's channel, not when the
   lingering link is up), **marking** (never mark the other host's channel), the **status poll**
   (lingering link = disconnected), the **release** (already on another host's channel: nothing to
   send, no disconnect), and the **late release** (only send a device that is on this host's channel).
3. **Late release only for a device the release couldn't send** (e.g. asleep at the switch). One
   this host sent away that comes back while the KVM is elsewhere was brought back on purpose
   (channel key or the other host): leave it. macOS tracks `sentAway`, cleared when the KVM returns.
4. **A channel switcher leaving while the KVM is here** (its channel key) is not an unexpected drop:
   no auto-reconnect.
5. A device that answers the channel query on its first connect of a session is a channel switcher
   right away (waited for, not called).

Name matching allows a stored name that is a cut-down copy (≥ 8 characters, a prefix of this
host's name), since devices cap the length. Windows writes `Dns.GetHostName()` and should compare
against the same string.

## Asks

- [ ] Mirror 1–5 in the Windows engine (at least the own-channel guard in the late release: it's
  what pulls the keyboard back).
- [ ] When both apps have it: one round trip each way with the K380 and the Reviung41, plus a hand
  switch each way while the KVM stays put. I'll do the Mac side of that test with the user.
