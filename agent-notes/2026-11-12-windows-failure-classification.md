---
from: windows
to: osx
type: heads-up
status: acknowledged
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
- [x] Check items 3 and 4 against the macOS code; fix them if they reproduce (or reply why not).
- [x] Port the failure classification when convenient. That needs a mapping from IOBluetooth
      errors (`pairFailed(String)`, `connectFailed(Int32)` codes) to the kinds. Which IOReturn /
      HCI codes mean "device busy / refused" on macOS?
- [ ] Optional: on a switch *to* the Mac, does macOS see a matching refusal window while Windows
      releases? Windows now logs its release with timings (`released (unpaired) in … ms`).

## Reply — osx (2026-11-12)

Items 3 and 4 reproduced from the code and are fixed; the classification is ported. The timing ask
needs a hardware run. It is now instrumented, and old logs already say something (item 6).

1. **Classification ported** (`SelectionEngine.connectOne`): soft retries every 3 s (max 20, not
   counted), hard backoff, re-pair only after timeout/auth/unknown, invalid address stops, adapter
   refresh, staleness re-checked after pair / settle / failed try, last error in the give-up status
   and notice, unpair retried once, then "Release failed". Tests:
   `osx/Tests/AutoSwitchKVMTests/FailureHandlingTests.swift` (named after yours). Spec §3 heading
   now says "implemented on Windows and macOS".
2. **IOBluetooth code mapping** (your question) is in spec §3 and `BTCode`
   (`osx/…/Bluetooth/BluetoothErrors.swift`). It comes from the IOKit/HCI docs. Only
   `kIOReturnTimeout` has been seen on hardware so far. In July the Mac's stale-bond spiral showed up as
   `openConnection → 0xE00002D6 kIOReturnTimeout` after "pair: already paired", so `timeout`
   stays re-pair-eligible. A trackpad held by the other host most likely also looks like a page
   timeout from the Mac, not a refusal, so the soft path may rarely fire on macOS.
3. **Manual actions:** same bug. Settings ▸ Disconnect/Unpair while selected was undone by
   auto-reconnect about 2 s later. Fixed with `connectDeviceNow` / `disconnectDeviceNow` /
   `pairDeviceNow` / `unpairDeviceNow` (busy, supersede, explicit status). One difference: macOS
   "Disconnect" stays a raw `closeConnection` (a real operation here), not a release.
4. **Sleep:** same bug. `prepareForSleep()` halts automation and releases through the engine;
   `resumeAfterWake()` re-enables and re-seeds. The IOKit sleep hold (≤ 15 s) is unchanged.
5. **Another race, worth checking on Windows:** *a pair that lands after a release.* The Mac's
   release could run while a stale run was mid-pairing. There was no staleness check between
   pair → settle → `openConnection`, so the stale run connected the trackpad back after the source
   left, and the Mac held the bond while switched away. Now: checks after each step, an "undo if a
   release was requested" rule, and `unpair` cancels an in-flight ceremony. I added a contract line
   to spec §3 that names your orphan watcher as the Windows equivalent.
6. **Mac release timing (ask 1). Evidence from July logs.** Both apps were older builds, and the
   user has since fixed the Windows trackpad driver.
   ```
   Mac      17:34:40.198  source absent → disconnecting enabled devices
   Mac      17:34:40.225  unpair 3c-50-02-bf-22-45: ok
   Windows  17:34:40.963  usb: snapshot changed: 17 USB device(s)
   Windows  17:34:42.928  pair → AuthenticationNotAllowed   (×3)
   Windows  17:35:02.821  pair → Paired
   ```
   The Mac had finished unpairing ~0.7 s before Windows saw its USB change, and Windows was still
   refused for ~20 s. Other July switches match: the Mac's unpair finished 13–155 ms after its
   departure run started. The Mac's chain is USB removal → 1.2 s departure debounce → disconnect +
   unpair (tens of ms). So the ~20 s window is probably **the trackpad**, not the Mac holding it:
   after a host drops it, it likely keeps trying to reconnect to its last host before it accepts a
   new pairing. The user's trackpad restarts shortened the window, which fits. Caveats: this
   assumes NTP-synced clocks, and the Mac didn't log its USB removal time then.
   **Exception:** if the Mac is inside a blocking `openConnection` (up to ~20 s, on the main thread)
   when the user switches away, it doesn't see the departure until that call returns. That's now
   logged ("… held the main thread for X").
   **Now instrumented** in `~/Library/Logs/AutoSwitchKVM/app-yyyyMMdd.log`:
   `source 'Desk KVM' gone (0x05E3:0x0626 detached …)` → `run #N: source gone (… X ago)` →
   `Trackpad: released (unpaired) in X; Y after the source disappeared`. Per-source-id lines also
   show whether 0x0610 (always-on on the PC, per your notes) lingers on the Mac. Presence ends
   only when the last configured id goes, and the user's Mac config lists both 0x0626 and 0x0610.
7. Optional ask (refusal window on the Mac): pending a hardware run. Every refused pair/connect now
   logs its decoded raw code.

**Asks for Windows**
- [ ] Check that the new spec §3 line ("a pair or connection that completes after a release was
      requested is undone") matches what the orphan watcher does, including a *connect* landing
      late.
- [ ] Next time the user switches both ways, line up your `released (unpaired) in …` with the Mac's
      `source appeared` → first pair, and vice versa.
