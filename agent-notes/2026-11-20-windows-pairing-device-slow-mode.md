---
from: windows
to: osx
type: request
status: open
date: 2026-11-20
related: [SPECIFICATION.md §3 "Connect flow" (A pairing device that never answers is tried slowly too), windows/src/AutoSwitchKVM.Core/SelectionEngine.cs (ConnectOneCoreAsync)]
---

# A pairing device that never answers now gets slow mode instead of giving up

**TL;DR:** On Windows the trackpad was stranded after a restart. The engine gave up after its
retry budget even though the KVM stayed on the PC, and nothing connected it afterwards. Windows
now tries a `managePairing` device whose pairing is never taken up about once a minute, for as
long as the KVM is here, with no give-up. Please check the Mac engine and port it if it gives up
the same way. Spec §3 marks it "macOS pending".

## What happened (PC time, 2026-11-20)

```
19:02 (Nov 19)  Trackpad connected; used normally for ~7 h
02:09:25        the Wireshark installer restarted Windows; the shutdown release unpaired the
                trackpad (BluetoothRemoveDevice OK, 3966 ms). The KVM stayed on the PC.
02:20:03        back up, run #1: pair -> "never answered (no pairing request)" x6 hard
                (+20 soft OperationInProgress while Windows' ceremony ran on), gave up 02:24:47
03:07-03:12     app restarted: the same, gave up again
```

No cable this time (no USB events for 05AC:0324). The trackpad sat unpaired for 11 min and
then answered no page. That's the "unpaired trackpad goes quiet" case, now with a clean example.
The user's point: with the KVM still here, the app should keep trying, so that a click (or
whatever wakes it) connects it. Before this change, slow mode excluded `managePairing` devices.

## What Windows changed

- **Classification:** a pairing that times out without the device ever asking (no
  `PairingRequested`) is `NoAnswer`, not `Timeout`. So no stale-bond unpair before the next try:
  the host holds no bond then, because an existing bond returns early. A pairing the device
  started but didn't finish stays `Timeout`.
- **Slow mode for pairing devices:** from 2 unanswered in a row, a try every 60 s
  (`SlowPairRetryMs`; one Windows pairing try holds the pairing for up to ~55 s), with the link
  watched in between. The status is Disconnected, and there's no "gave up". The log line, once:
  `doesn't answer pairing (asleep or switched off?). Click or touch it to wake it; it's tried
  every 60 s while the KVM is here`.
- **Unlike non-pairing devices, it isn't sticky:** a refusal (AuthenticationFailure,
  not-allowed, …) during slow mode goes back to the normal budget and can give up, so a real
  problem stays visible.
- **The unanswered count restarts with each connect for a pairing device**, because it may have
  been awake on the Mac since. The status panel says "Asleep: wake it to connect" only while the
  KVM is here; after the KVM leaves it's "Released".
- Manual Connect still gives up after `connectRetryMax`.
- Tests: `FailureHandlingTests.A_pairing_device_that_never_answers_is_tried_slowly_and_pairs_when_it_answers`,
  `…_in_slow_mode_that_refuses_gets_the_normal_budget`, `Manual_connect_of_a_pairing_device_that_never_answers_still_gives_up`.

## Asks

- [ ] Does the Mac engine give up on the trackpad when its pairing gets no answer (page timeout /
      pairing timeout) while the KVM stays on the Mac? If so, port the above, with the interval
      that suits the Mac's radio queue, and mark spec §3 done for macOS.
- [ ] What does the Mac see when it pages an unpaired, asleep trackpad: a page timeout, a
      pairing timeout, or something else? That decides which failure counts as unanswered there.
      Careful: spec §3 says the Mac's stale-bond case also shows up as `kIOReturnTimeout`, so
      `timeout` must stay re-pair-eligible. Only a failure that can't be a stale bond (e.g. no
      bond on the Mac when the pairing started) should count as unanswered.
- [ ] Not changed: the shutdown/restart release itself. The user prefers to keep it; the fix
      is to keep trying afterwards.
