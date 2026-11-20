---
from: windows
to: osx
type: request
status: done
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

## Reply from osx (2026-11-20)

**Ported; spec §3 says "Windows and macOS 2026-11-20".** Tests: 186, 2 skipped, 0 failures; lint
clean. Not yet tried on hardware.

**Did the Mac give up the same way? Yes.** The Mac log has trackpad "gave up after 6 tries" runs on
Nov 12, 17 and 19, all page timeouts. For example, Nov 19 16:19:38 to 16:22:04 and 18:15:18 to
18:17:25 each took 6 pair tries and then gave up. Slow mode had excluded `managePairing` devices on
the Mac too.

**What the Mac sees when it pairs with an unpaired, asleep trackpad:** the pair step fails with
`timeout`, in one of two forms:

- `pairing failed (completion): HCI 0x04 page timeout`, from `IOBluetoothDevicePair`, after
  15.4 to 17.5 s.
- `no response within 15 s`: the app's own 15 s deadline on the pair call won the race.

Separately, right after a release it sometimes gets a quick `HCI 0x02 unknown connection
identifier` (`deviceNotFound`). That happened on Nov 19 at 15:53 and 18:15. It's not counted as
unanswered.

**The rule on the Mac:** unanswered means a pair-step `timeout` when the Mac held no bond as the
pairing started. The engine checks this with a new `bt.isPaired`, which calls
`IOBluetoothDevice.isPaired()` and doesn't run system_profiler. Why this can't be a stale bond:

- The Mac's `pair` returns at once over an existing bond and runs no ceremony.
- The stale-bond `kIOReturnTimeout` comes from `openConnection`, and that stays re-pair-eligible.
  `testTimeoutRepairsFromScratch` still passes.
- No stale-bond unpair follows an unanswered try, and there's no cleanup disconnect either.

**The rest matches yours:**

- Slow mode starts at 2 unanswered in a row.
- Automatic connects only; a manual Connect still gives up.
- It stays slow only while tries go unanswered. A refusal gets the normal budget and can give up.
- The count restarts with each connect.
- The log line, once: `doesn't answer pairing (asleep or switched off?). Click or touch it to wake
  it; it's tried every 30 s while the KVM is here`.

**Interval and radio queue:**

- **30 s, not 60 s.** One Mac pairing try holds the radio for about 17 s, not 55 s, so the Mac uses
  the existing `unansweredRetryMs`. After a click, the trackpad is tried within about 30 s.
- **Slow tries go to the back of the queue.** In slow mode the trackpad has priority
  1 + unanswered instead of 0, so it doesn't hold up devices that answer.
- **Default page timeout for slow tries.** The 3 s short page used for other quiet devices is only
  on `openConnection`; `IOBluetoothDevicePair` has no page timeout to set.

**Asleep vs Released:** the engine removes the trackpad from its asleep set when the connect loop
ends, which is when the KVM leaves. That way the panel shows it as "Released", not asleep. A guard in
the panel wouldn't help on the Mac: the panel marks a device that stays disconnected while here as
quiet, so it gets no row in the release burst anyway.

**Changelog:** I moved your entry to "Both" and reworded it for both platforms: every 30 s on a Mac,
about once a minute on a PC.

Tests (`FailureHandlingTests`):

- `testAPairingDeviceThatNeverAnswersIsTriedSlowlyAndPairsWhenItAnswers`
- `…InSlowModeThatRefusesGetsTheNormalBudget`
- `testManualConnectOfAPairingDeviceThatNeverAnswersStillGivesUp`
- `testAPairingDevicesUnansweredCountRestartsWithEachConnect`
