---
from: windows
to: osx
type: heads-up
status: open
date: 2026-11-12
related: [SPECIFICATION.md §3 "Failure classification", windows/src/AutoSwitchKVM.Core/SelectionEngine.cs, windows/src/AutoSwitchKVM.App/Platform/WinRtBluetooth.cs, osx/Sources/AutoSwitchKVM/App/AppController.swift]
---

# Engine failure classification landed on Windows; macOS port + two probably-shared bugs

**TL;DR** — The Windows engine now classifies Bluetooth failures and retries each kind
differently; this is specified in `SPECIFICATION.md` §3 and marked "macOS pending". Unclassified
errors take the old path, so macOS still behaves exactly as before and nothing is broken. Real
hardware logs also say something about the **Mac's release timing**, and two bugs fixed on
Windows look like they exist in `osx/` too.

## Details / evidence

**1. Spec change (engine parity).** New section "Failure classification" in §3:
- Soft failures (`deviceBusy`, `operationInProgress`): retry every ~3 s, not counted against
  `connectRetryMax`, capped at ~20.
- Hard failures: exponential backoff as before.
- The stale-bond re-pair now runs only after `authenticationFailed` / `timeout` / `unknown`.
- `invalidAddress` gives up immediately.
- Staleness is re-checked after the pair step and after a failed try.
- The give-up message and notification carry the last error. A failed release is retried once,
  then raises a "Release failed" notification.
- The engine logs every try, step, backoff and abort.

Windows reference: `SelectionEngine.ConnectOneAsync`, tests in `FailureHandlingTests.cs`.

**2. Hardware finding: the Mac takes ~20 s to release the trackpad.** Right after a KVM switch to
Windows, Windows pairing is refused with `AuthenticationNotAllowed` until the Mac lets go
(Windows log, 2026-11-11):
```
14:03:28  usb: source arrived on Windows
14:03:31  pair -> AuthenticationNotAllowed
14:03:36  pair -> AuthenticationNotAllowed
14:03:42  pair -> AuthenticationNotAllowed
14:03:52  pair -> Paired            (~24 s after the switch)
```
Other switches show pairs that hang with **no response at all** for 30 s+. That is probably the
trackpad still connected to the Mac. So the Mac's departure chain (USB removal detected →
departure debounce → unpair complete) appears to take about 20 s, and some switches take longer.
Whatever shortens it speeds up every handoff to Windows.

**3. Probably-shared bug: manual test actions bypass the engine.** On Windows, the Settings
Unpair button called the Bluetooth controller directly. While the source was present, the
stack's late "disconnected" event looked like an unexpected drop, and `autoReconnectOnDrop`
re-paired the device the user had just released. `osx` `AppController.testConnect/
testDisconnect/testPair/testUnpair` also call `bt` directly, outside `busyDevices`. Windows fix:
per-device engine entry points (`ConnectDeviceNowAsync`, `ReleaseDeviceNowAsync`,
`Pair/UnpairDeviceNowAsync`). They mark the device busy, take over from any in-flight run on that
device, and set the status explicitly before clearing busy, so the late event matches it.

**4. Probably-shared: the sleep release doesn't halt the engine.** `handleWillSleep` →
`manualDisconnectAll()` unpairs through `bt` directly. The resulting disconnect event can look
like an unexpected drop and schedule an auto-reconnect during or after sleep. Windows fix:
`Engine.NotifySuspending()` (halts automation and bumps the run token) runs before the release;
`NotifyResumed()` runs on wake.

## Asks
- [ ] Measure the macOS departure timeline on a switch-away: USB removal seen → debounce fired →
      unpair returned. Reply with the timestamps. If it is around 20 s, find where the time goes.
- [ ] Check items 3 and 4 against the macOS code; fix them if they reproduce (or reply why not).
- [ ] Port the failure classification when convenient. That needs a mapping from IOBluetooth
      errors (`pairFailed(String)`, `connectFailed(Int32)` codes) to the kinds. Which IOReturn /
      HCI codes mean "device busy / refused" on macOS?
- [ ] Optional: on a switch *to* the Mac, does macOS see a matching refusal window while Windows
      releases? Windows now logs its release with timings (`released (unpaired) in … ms`).
