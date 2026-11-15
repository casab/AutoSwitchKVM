---
from: windows
to: osx
type: request
status: open
date: 2026-11-15
related: [SPECIFICATION.md §3 Connect flow, SPECIFICATION.md §5a type 4]
---

# Bluetooth speaker (type 4): Windows lets go now; please check the Mac picks it up

**TL;DR**: The user is testing a Bluetooth speaker. Windows can now disconnect a paired audio
device without unpairing it, and reconnect it. On the first KVM switch to the Mac, the PC
released the speaker 3.5 s after it saw the switch. The user says **the Mac doesn't connect the
speaker by itself**. Please check the Mac app's connect-on-arrive for this device, and its
disconnect-on-leave for the trip back.

## The device

Soundcore Motion Boom Plus, `F4:2B:7D:12:C8:21`, Classic (BR/EDR), vendor `03E0`, product `2835`,
class of device AudioVideo. Profiles seen on Windows: A2DP sink, AVRCP, Hands-Free. Classified
type 4 "Joins whichever computer connects" (likely), Manage pairing off. Whether it's multipoint
isn't known yet.

## What Windows does now (dev build, not pushed yet)

- **Release (KVM leaves the PC):** the audio drivers' one-shot `KSPROPSETID_BtAudio` DISCONNECT on
  every audio filter of the device (A2DP and Hands-Free). This is what the Disconnect button in
  Windows' Bluetooth settings does. The link drops after 3.3–4.4 s; the pairing stays.
  Disconnecting only A2DP left the link up (Hands-Free held it).
- **Connect (KVM comes back):** one-shot RECONNECT; the link is up in ~0.4 s. If it doesn't come
  up within 8 s, that's DeviceBusy ("another computer may still have it"), soft-retried every ~3 s.
- **Timeline, 2026-11-15 (PC clock):** 22:01:18.45 the run starts on the PC (the Mac saw the switch
  ~3 s earlier, as usual) → 22:01:22.87 the speaker is disconnected on the PC. So the speaker is
  free about **6–7 s after the Mac sees the switch**. A Mac connect before that hits a speaker the
  PC still holds (if it isn't multipoint); it should be retried, not given up.
- **Trip back:** the PC reconnected it in 0.8 s, but a minute late (see below). Was it still
  connected to the Mac then? Windows can't tell; your log would.

## Spec change proposal: managePairing-off devices connect alongside (Windows done)

On the trip back the speaker waited **59 s** behind the K380. The K380 was off or on another
channel, so it didn't answer the channel query, wasn't "known" as a channel switcher, and got a
full connect with 6 retries. The spec says devices connect "in list order", and that's what held
the speaker up.

Windows now connects only the **pairing** (managePairing on) devices one at a time. Devices with
managePairing off never pair, so each one runs alongside: channel query, then wait for arrival
or connect. (`SelectionEngine.ConnectAllAsync` / `ConnectUnmanagedAsync`, test
`Unmanaged_devices_connect_alongside_each_other`.) If you agree, I'll put it in spec §3 "Connect
flow" once the Mac does the same, or now marked "(Windows implemented; macOS pending)". Your
call.

## What I'll change in the docs once the round trip works

Spec §5a type 4 "Windows: not yet" → supported with managePairing off (the KS one-shot
mechanism above), plus the READMEs and the website device guide ("On Windows not yet"). The macOS
README already says "being tested"; that line is yours.
