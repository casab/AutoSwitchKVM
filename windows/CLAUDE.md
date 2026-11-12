# CLAUDE.md

Guidance for Claude when working on the **Windows** app. Cross-platform overview is in
`../CLAUDE.md`; the behavior contract is `../SPECIFICATION.md`; the macOS reference implementation
is `../osx/`.

## What this is

**AutoSwitch KVM for Windows** — a native, background **system-tray** app (C#/.NET 8 + WinUI 3) with
feature parity to the macOS app: detect a USB "switcher source" appearing/disappearing and
pair/connect (or unpair/disconnect) the configured Bluetooth devices, with the same model (profiles,
per-device pairing management, timing, global shortcuts).

Status: **M0 validated on real hardware; M1-M7 implemented and running on-device.** Current focus
is handoff reliability. Pairing after a switch was the weak point (refusals while the Mac still holds
the trackpad; timed-out pairs that kept running inside Windows). The engine now classifies failures
and logs every step. The full milestone roadmap and per-component status live in `PLAN.md`; the
proven prototype is `reference/Trackpad-AutoSwitch.ps1` (the Windows counterpart of the macOS
Hammerspoon script).

**Logs to read when debugging a handoff:** `%LOCALAPPDATA%\AutoSwitchKVM\logs\app-yyyyMMdd.log`
(config: `%LOCALAPPDATA%\AutoSwitchKVM\config.json`). Categories: `engine` (runs, tries, retries,
aborts, give-ups), `bt` (WinRT/Win32 calls with timings and raw statuses), `usb`, `power`, `app`.

**Trackpad driver traces:** the Magic Trackpad's Windows driver is vitoplantamura's
`MagicTrackpad2ForWindows` (checkout + Microsoft-signed package with PDBs in `reference_win_driver/`,
untracked; don't commit it). Its Bluetooth filter (`AmtPtpHidFilter`) is loaded only while Windows
holds the bond. It configures multitouch (SET_FEATURE `0xF1`) 1 s after start, retrying every 2 s,
and polls the battery every 60 s. `tools/driver-trace.ps1` captures its WPP traces
(`start`/`stop` need an admin shell) and decodes them with the PDB to
`logs/driver-<timestamp>.txt`, with local timestamps that line up with the app log.

## Coordinating with the macOS agent

Another agent usually works on `osx/` in parallel, on a Mac. Use `agent-notes/` at the repo root
(protocol: `../agent-notes/README.md`). At session start, `git pull --ff-only` and check open
notes `to: windows|all`. Write a note `to: osx` when you change `SPECIFICATION.md`/config keys,
see trackpad/Mac behavior in the Windows logs (e.g. how long the Mac takes to release), or fix a
bug whose pattern likely exists in `osx/`. Tell the user when a note is waiting to be
committed/pushed. Don't edit `osx/` yourself unless the user asks.

## Build & run

Requires **Visual Studio 2022** (.NET Desktop workload + Windows App SDK C# templates) and the
**.NET 8 SDK**. Open `AutoSwitchKVM.sln`, set **AutoSwitchKVM.App** as startup, platform **x64**, Run
→ a tray icon appears; click it (or right-click → Open Settings) for the full Settings window.

- App is **unpackaged** (`WindowsPackageType=None`); the Windows App SDK bootstrapper auto-initializes.
- NuGet versions in the `.csproj` files are a known-good baseline — bump if restore complains.

**Compile the WinUI app only on Windows** — non-Windows sandboxes do not have the Windows App SDK /
WinUI stack. The app build is now verified on Windows; if a running tray app locks the normal output
DLLs, build to a clean output path or exit the tray app first. The **Core lib + tests are plain
`net8.0`** and can be built/tested anywhere a .NET 8 SDK exists.

## Test

```sh
dotnet test tests/AutoSwitchKVM.Core.Tests
```

The Core library (models/config/engine) is the portable, high-value test target — xUnit with fake
USB + Bluetooth (`tests/.../Fakes/`), no hardware. Platform services (PnP, WinRT BT, hotkeys, toasts)
are validated on-device, like the macOS Bluetooth layer.

Unit coverage so far (mirrors the macOS suite):
- `ModelsTests` - address digits, source display, DeviceStatus labels/equality, UsbDeviceInfo
  display, AppConfig defaults + `Normalized()`.
- `ConfigTests` - defaults, save/load round-trip, default-on-missing, corrupt-file fallback,
  camelCase JSON key parity with macOS, dock/paused persistence, atomic/debounced writes, absent-vs-null hotkeys.
- `ProfilesTests` - active-profile accessors, multi-profile round-trip, legacy single-source migration.
- `SelectionEngineTests` - the full engine state machine.
- `FailureHandlingTests` - failure classification (soft vs hard retries, fresh-bond rules,
  timeout, invalid address), logging of every failure, release retry, manual actions routed
  through the engine, suspend/resume.
- `SourceLearnerTests` - appear/disappear detection, steady-device ignore, cancel.

Not unit-tested (validated on-device / by the spikes, by design): `PnpUsbMonitor` (Spike 3),
`WinRtBluetooth` (Spikes 1-2), and future hotkey/toast/login services.

## Architecture (solution layout)

Split into a portable core + a thin platform app (the split macOS skipped; worth it here for
testability):

- `src/AutoSwitchKVM.Core/` — `net8.0` class library, **no WinUI/WinRT-UI deps**.
  - `Models/Models.cs` — `USBSource`, `BTDevice`, `Profile`, `KeyShortcut`, `AppConfig`. Ports the
    macOS model 1:1. `AppConfig` holds `Profiles` + `ActiveProfileID`; `Source`/`Devices` are
    `[JsonIgnore]` computed accessors onto the active profile (call sites stay simple).
  - `ConfigStore.cs` — JSON at `%LOCALAPPDATA%\AutoSwitchKVM\config.json`. **camelCase naming policy
    keeps JSON keys identical to the macOS app** for config parity. Default-on-missing comes free
    from property initializers; writes are atomic, app edits are debounced, legacy top-level
    source/devices migrate to a Default profile, and absent hotkeys default while present-null means cleared.
  - `IUsbMonitor.cs`, `IBluetoothController.cs` — the platform seams (mirror the macOS protocols and
    `SPECIFICATION.md` §5). The Bluetooth interface's doc comments carry the exact Milestone 0 recipe.
  - `SourceLearner.cs` — **ported (Milestone 3)**; snapshot-diff Learn logic (baseline at Start,
    accumulate appeared/disappeared keys, resolve candidates at Finish). Tested with FakeUsbMonitor.
  - `SelectionEngine.cs` + `DeviceStatus.cs` — **ported (Milestone 2)** from the macOS state
    machine, plus **failure classification** (soft retries for busy/in-progress, which don't
    spend `connectRetryMax`; stale-bond re-pair only after auth-failed/timeout/unknown; see
    SPECIFICATION.md §3). Also has per-device manual entry points
    (`ConnectDeviceNowAsync`/`ReleaseDeviceNowAsync`/`Pair|UnpairDeviceNowAsync`), suspend
    handling (`NotifySuspending`/`NotifyResumed`), and structured logging through `ILogSink`
    (`ILogSink.cs`). Drive it via `HandleUsb`/`ApplyUsbSnapshot` + `EvaluateNowAsync` (no-timer
    test seams); production uses `OnUsbEvent`/`OnUsbSnapshot` (debounced) + `PollStatusesAsync`.
    Internal seams for tests: `SleepHook` (instant delays), `CallTimeoutOverride`,
    `SoftRetryIntervalMs`/`MaxSoftRetries`.
  - `IBluetoothController.cs` also defines `BluetoothFailureKind` + `BluetoothException.Kind/Detail`:
    the backend classifies, the engine picks the retry policy.
- `src/AutoSwitchKVM.App/` — `net8.0-windows10.0.19041.0`, WinUI 3, unpackaged.
  - `Services/AppController.cs` — **the coordinator (Milestone 5)**: owns config + engine + USB
    monitor + Bluetooth controller, starts everything, exposes state + actions (connect/disconnect
    all, pause, switch profile, per-device actions, learner, config mutate/save). Lives on the UI
    thread; monitors marshal callbacks to its SynchronizationContext and the poll via DispatcherQueue.
    Per-device actions route through the engine. Fire-and-forget calls go through `Fire` (logs
    faults). Wires the engine's log to `Support/AppLogSink.cs`.
  - `Support/DebugLog.cs` — in-memory categorized log buffer (Diagnostics tab).
  - `App.xaml(.cs)` — tray-only app via **H.NotifyIcon** with a **dynamic context menu** rebuilt from
    AppController state on open (status, profiles, device rows, quick actions, Settings, Exit). The
    rich-flyout-window approach was deliberately skipped (finicky/untestable blind); the menu covers
    the same surface reliably.
  - `SettingsWindow.xaml(.cs)` — profile bar + 5 tabs built in **code-behind** (avoids x:Bind compile surprises):
    Source / Devices / General / Extras / Diagnostics. `RelayCommand.cs` is a minimal `ICommand`.
  - `Platform/PnpUsbMonitor.cs` — **implemented (Milestone 3)** via `System.Management`: Win32_PnPEntity
    enumeration + Win32_DeviceChangeEvent wakeups (debounced) + ~2s reconcile; emits on VID/PID-set
    change, marshaled to the captured SynchronizationContext.
  - `Platform/{LoginItem,PowerMonitor,HotKeyService,ToastNotifier}.cs` — **system integration
    (Milestone 6)**: HKCU Run key; SystemEvents power suspend/resume (suspend->disconnect all,
    resume->reconcile); Win32 RegisterHotKey on a message-only window (defaults Ctrl+Alt+P/C/D);
    AppNotificationManager toasts (unpackaged Register). Wired in AppController; recorder in the
    General tab. (Note: namespaced under `Platform`, not `System`, to avoid clashing with `System.*`.)
  - `Platform/WinRtBluetooth.cs` — **implemented (Milestone 4)**: radio power, ConnectionStatus+HID
    `isConnected`, idempotent `pair` (resolve endpoint by address + Custom.PairAsync ConfirmOnly
    auto-accept), `unpair`=`BluetoothRemoveDevice` first + WinRT fallback, `connect`=ensure-paired,
    `disconnect`=no-op (Classic HID: release is unpair), paired-list, ConnectionStatusChanged
    monitoring. Maps `DevicePairingResultStatus` → `BluetoothFailureKind` (`KindFor`). Tracks a
    timed-out pairing that is still running ("orphan"; see Gotchas), and records per-device
    pair/release intent so a queued pair steps aside for a newer release. **Key mapping:** the
    engine's mid-connect `disconnect` is a deliberate no-op and `pair` is idempotent, so the shared
    connect/disconnect flow resolves correctly on Windows.
- `tests/AutoSwitchKVM.Core.Tests/` — xUnit; `ConfigTests.cs` + `Fakes/Fake{Usb,Bluetooth}*`.

CI: a `windows-latest` job in `.github/workflows/ci.yml` runs `dotnet test` on the Core tests and
builds the WinUI app (x64 Release). Push builds run on `main`; PRs always build.

## Milestone 0 findings — the validated Bluetooth/USB contract

Validated against the real Magic Trackpad (MAC `3C:50:02:BF:22:45`, Win 11, PowerShell 5.1) via
`spikes/Spike1-ReadState.ps1` and `spikes/Spike2-Pairing.ps1`. Full detail in `PLAN.md`.

- **isPoweredOn** = `Windows.Devices.Radios.Radio` Bluetooth `State == On`.
- **isConnected** = `BluetoothDevice.FromBluetoothAddressAsync(addr).ConnectionStatus == Connected`
  (primary), corroborated by a `BTHENUM\...DEV_<macDigits>` HID PnP node. Both flip cleanly on handoff.
- **connect (= pair)** for Classic-HID: the cached device is NOT pairable (instant `Failed`).
  Discover the **unpaired endpoint** first — `DeviceInformation.FindAllAsync(
  BluetoothDevice.GetDeviceSelectorFromPairingState(false))`, match by MAC (the endpoint `Id` carries
  the MAC **with colons** — strip separators) — then
  `endpoint.Pairing.Custom.PairAsync(DevicePairingKinds.ConfirmOnly)` with a `PairingRequested`
  handler that calls `args.Accept()`. **Do NOT gate on `CanPair`** (reads false yet pairing
  succeeds). ~14s typical.
- **disconnect (= unpair/release)** was first validated with `Pairing.UnpairAsync()` (~3s), but the app now uses Win32 `BluetoothRemoveDevice` first because it tears down the active link as well as the bond; WinRT `UnpairAsync()` is fallback.
- **Authoritative paired check** = `FromBluetoothAddressAsync(...).DeviceInformation.Pairing.IsPaired`.
  Do NOT trust the `FindAll`-enumeration's `IsPaired` (reported false while connected).

## Conventions

- New external dependencies (USB, Bluetooth, hotkeys, toasts) go **behind an interface** in Core
  (like `IBluetoothController`/`IUsbMonitor`) so they're mockable. Add tests when you touch the engine.
- `async`/`await` throughout; pass `CancellationToken`. Marshal to the UI thread via `DispatcherQueue`.
- Keep config JSON keys **identical to macOS** (camelCase) — don't rename without updating both.
- A CLI fallback (PolarGoose `BluetoothDevicePairing.exe`) stays a documented option behind
  `IBluetoothController`, mirroring the macOS native-with-`blueutil`-fallback design. Not needed for
  the trackpad (WinRT custom pairing works) — keep it as the escape hatch only.

## Gotchas

- **The bond is exclusive.** While Windows holds the pairing the Mac can't connect, so the handoff is
  pair-on-arrive / **unpair-on-leave** — there is no passive "disconnect" for Classic HID, and
  `disconnect` ≡ unpair. `managePairing` is effectively **mandatory**, not optional, for such devices.
- **The release can land AFTER the other host re-paired** (observed on hardware): the departure
  chain (USB storm → snapshot → departure debounce → `BluetoothRemoveDevice`) takes several
  seconds, and a fast Mac re-pairs first — the late removal then disturbs the device and can
  leave the Mac holding a bond the device no longer honors. Both engines self-heal (retry ≥ 2
  re-pairs from scratch; `autoReconnectOnDrop` recovers mid-session drops); the structural fix is
  the planned LAN release-ack handshake.
- **Release with the Win32 `BluetoothRemoveDevice`, not just WinRT `UnpairAsync`.** WinRT unpair
  removes the pairing record on the Windows side but can leave the device attached at the radio level,
  so the trackpad won't release to the other host. `BluetoothRemoveDevice` (the classic "Remove
  device" API) tears down the active ACL link (LMP detach) *and* removes the bond - that's what
  actually frees the device for the Mac. `UnpairAsync` is kept only as a fallback.
- **`PairAsync` needs the discovered endpoint, custom ConfirmOnly, and a real callback.** Basic
  `Pairing.PairAsync()` instant-fails this device. A PowerShell **scriptblock** `PairingRequested`
  handler deadlocks (single-threaded runspace) → `RejectedByHandler`; the spike uses a compiled
  delegate. In the C# app this is a non-issue (the callback runs on a threadpool thread).
- **Pair is slow (~14s)** — set the per-call timeout generously (Core default `BtCallTimeoutSecs=45`,
  vs macOS 15). The Mac-release race (`CanPair=false` / not discoverable while the Mac still holds it)
  is absorbed by the engine's bounded retry + backoff; re-discover the endpoint on each attempt.
- **Source signal nuance** (from the prototype): the KVM hub exposes an always-on instance
  (`PID_0610`) plus one that disappears on switch-away (`PID_0626`). Prefer the disappearing PID as
  the signal. Use device-change notifications **plus a ~2s reconcile timer** — the USB
  re-enumeration storm on a KVM switch starves a pure debounce.
- **Spike scripts must be pure ASCII.** PowerShell 5.1 reads BOM-less `.ps1` as Windows-1252; a
  non-ASCII char (e.g. an em-dash) gets misread as a smart-quote and breaks parsing.
- **Serialize Bluetooth ops.** `WinRtBluetooth` guards pair/unpair with a `SemaphoreSlim` - without
  it, the auto-connect, the engine's retries, and manual test-connect run concurrent radio inquiries
  and produce `AuthenticationTimeout` churn (the "sometimes connects" instability seen on first run).
- **Do NOT run a discovery inquiry to pair - pair the resolved endpoint directly.** Diagnostics
  proved `BluetoothDevice.FromBluetoothAddressAsync(addr)` returns the pairable AssociationEndpoint
  **instantly** (~10ms, `CanPair=True`, same `Id` the inquiry would yield). `PairAsync` calls
  `dev.DeviceInformation.Pairing.Custom.PairAsync(ConfirmOnly|...)` on it - no `FindAllAsync` /
  `DeviceWatcher`. The `GetDeviceSelectorFromPairingState(false)` selector forces `IssueInquiry:=True`,
  which blocks ~30s (and the engine's per-call timeout then cancelled it mid-pair) - that was the
  whole cause of "auto never connects, manual-after-waiting does". Discovery (`FindAllAsync`) is only
  used now by the "Diagnose" button, not the pairing path.
- **Tray menu: use `ContextMenuMode.PopupMenu` + `Command`, not `Click`.** The XAML `SecondWindow`
  flyout renders as a blank rectangle (the secondary window doesn't inherit the app theme); the
  native popup renders reliably. In PopupMenu mode the flyout is converted to a Win32 menu:
  `MenuFlyout.Opening` never fires (rebuild the menu from `RightClickCommand` instead) and item
  selection executes the item's `ICommand` — `Click` handlers are never raised.
- **Size the Settings window** via `AppWindow.Resize` - a WinUI `Window` opens very large by default.
- **WinRT pairing ignores cancellation.** Cancelling `Custom.PairAsync` (incl. via `AsTask(ct)`)
  does not stop the ceremony; it runs inside Windows until its own ~45-60 s timeout. Releasing the
  op lock on our timeout made every retry fail instantly with `OperationAlreadyInProgress` (64 in
  one day's log). `WinRtBluetooth` now keeps a timed-out ceremony as an *orphan*. While it runs,
  a new pair fails fast with `OperationInProgress`, and the engine soft-retries every ~3 s.
  (Waiting inside the call ate the caller's timeout: a retry waited 19 s and had 13 s left to
  pair.) A watcher logs the orphan's outcome and unpairs again if it completes `Paired` after a
  release was requested. On hardware an orphan ends ~50-55 s after it started, as `Canceled`.
- **A "Connected" link is not a working trackpad.** `ConnectAsync` waits (≤ 10 s) for the HID
  chain: the `BTHENUM\{00001124-…}…<MAC>` service node and at least one
  `HID\{00001124-…}_VID&…_PID&…` collection started (`ConfigManagerErrorCode == 0`). Only for
  Peripheral class-of-device. Otherwise it throws `HidNotReady`; two in a run end it with a
  power-cycle hint. Background: the trackpad wedged (HID never started, power cycle needed) when
  one host paired it while the other was still releasing it.
- **USB detection lag.** On 2026-11-12 Windows saw each KVM switch 3.1-3.8 s after the Mac.
  `PnpUsbMonitor` now reconciles on the first event of a burst (leading edge) as well as after
  it, and logs `via <trigger>, enumerate N ms, M ms after the burst started`. Use that to
  attribute the remaining lag.
- **`BluetoothRemoveDevice` is synchronous and slow (~3.4 s)** because it waits for the link
  teardown. It runs via `Task.Run`; called inline from the engine it froze the UI thread and kept
  the engine's timeout from firing.
- **Classify Bluetooth failures** (`BluetoothException.Kind`, mapped from `DevicePairingResultStatus`
  in `WinRtBluetooth.KindFor`). `AuthenticationNotAllowed` right after a KVM switch means the Mac
  still holds the trackpad: the engine soft-retries it every ~3 s without spending
  `connectRetryMax` (see SPECIFICATION.md §3 "Failure classification").
- **Manual actions go through the engine** (`ConnectDeviceNowAsync`, `ReleaseDeviceNowAsync`,
  `Pair/UnpairDeviceNowAsync`). Calling `_bt` directly bypassed the busy guard: a manual unpair
  while selected produced a late "disconnected" event that looked like an unexpected drop, and
  auto-reconnect re-paired the device. Only the suspend release calls `_bt` directly (it must
  run off the UI thread), after `Engine.NotifySuspending()` halts automation.
- **Log through `ILogSink`** in Core (the app wires `AppLogSink`). No silent `catch { }` on
  Bluetooth paths: best-effort steps log a warning; fire-and-forget calls go through
  `RunLoggedAsync`/`Fire` so a fault is logged instead of becoming an unobserved task exception.

## When making changes

- Touching the engine, config, or models → update/extend `tests/AutoSwitchKVM.Core.Tests`.
- Adding a config field → rely on property initializers for default-on-missing; keep the JSON key in
  sync with the macOS model. Preserve absent-vs-null semantics for nullable settings such as hotkeys.
- Follow `PLAN.md` milestones; update its status markers as milestones land.
