# CLAUDE.md

Guidance for Claude when working on the **Windows** app. Cross-platform overview is in
`../CLAUDE.md`; the behavior contract is `../SPECIFICATION.md`; the macOS reference implementation
is `../osx/`.

## What this is

**AutoSwitch KVM for Windows** — a native, background **system-tray** app (C#/.NET 8 + WinUI 3) with
feature parity to the macOS app: detect a USB "switcher source" appearing/disappearing and
pair/connect (or unpair/disconnect) the configured Bluetooth devices, with the same model (profiles,
per-device pairing management, timing, global shortcuts).

Status: **M0 validated on real hardware; M1-M7 authored** (Core ported and unit-tested; platform
layer, tray UI, and Settings built) - pending the first on-device build. The full milestone roadmap
and per-component status live in `PLAN.md`; the proven prototype is `reference/Trackpad-AutoSwitch.ps1`
(the Windows counterpart of the macOS Hammerspoon script).

## Build & run

Requires **Visual Studio 2022** (.NET Desktop workload + Windows App SDK C# templates) and the
**.NET 8 SDK**. Open `AutoSwitchKVM.sln`, set **AutoSwitchKVM.App** as startup, platform **x64**, Run
→ a tray icon appears; click it (or right-click → Open Settings) for the empty Settings window.

- App is **unpackaged** (`WindowsPackageType=None`); the Windows App SDK bootstrapper auto-initializes.
- NuGet versions in the `.csproj` files are a known-good baseline — bump if restore complains.

**Do not attempt to compile the WinUI app in a non-Windows sandbox** — there's no Windows App SDK /
WinUI there. Write carefully, review, and hand builds to a Windows machine. The **Core lib + tests
are plain `net8.0`** and *can* be built/tested anywhere a .NET 8 SDK exists.

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
  camelCase JSON key parity with macOS, dock/paused persistence.
- `ProfilesTests` - active-profile accessors, multi-profile round-trip, legacy single-source migration.
- `SelectionEngineTests` (16) - the full engine state machine.
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
    from property initializers. (TODO M2: atomic+debounced write, absent-vs-null hotkey nuance, and
    the legacy single-source migration the macOS `Codable` does.)
  - `IUsbMonitor.cs`, `IBluetoothController.cs` — the platform seams (mirror the macOS protocols and
    `SPECIFICATION.md` §5). The Bluetooth interface's doc comments carry the exact Milestone 0 recipe.
  - `SourceLearner.cs` — **ported (Milestone 3)**; snapshot-diff Learn logic (baseline at Start,
    accumulate appeared/disappeared keys, resolve candidates at Finish). Tested with FakeUsbMonitor.
  - `SelectionEngine.cs` + `DeviceStatus.cs` — **ported (Milestone 2)**, 1:1 from the macOS state
    machine. Drive it via `HandleUsb`/`ApplyUsbSnapshot` + `EvaluateNowAsync` (no-timer test seams);
    production uses `OnUsbEvent`/`OnUsbSnapshot` (debounced) + `PollStatusesAsync`. `SleepHook`
    (internal) makes per-device delay / post-pair settle / retry backoff instant in tests.
- `src/AutoSwitchKVM.App/` — `net8.0-windows10.0.19041.0`, WinUI 3, unpackaged.
  - `Services/AppController.cs` — **the coordinator (Milestone 5)**: owns config + engine + USB
    monitor + Bluetooth controller, starts everything, exposes state + actions (connect/disconnect
    all, pause, switch profile, per-device test, learner, config mutate/save). Lives on the UI
    thread; monitors marshal callbacks to its SynchronizationContext and the poll via DispatcherQueue.
  - `Support/DebugLog.cs` — in-memory categorized log buffer (Diagnostics tab).
  - `App.xaml(.cs)` — tray-only app via **H.NotifyIcon** with a **dynamic context menu** rebuilt from
    AppController state on open (status, profiles, device rows, quick actions, Settings, Exit). The
    rich-flyout-window approach was deliberately skipped (finicky/untestable blind); the menu covers
    the same surface reliably.
  - `SettingsWindow.xaml(.cs)` — 5 tabs built in **code-behind** (avoids x:Bind compile surprises):
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
    `isConnected`, idempotent `pair` (discover endpoint + Custom.PairAsync ConfirmOnly auto-accept),
    `unpair`, `connect`=ensure-paired, `disconnect`=no-op (Classic HID: release is unpair),
    paired-list, ConnectionStatusChanged monitoring. **Key mapping:** the engine's mid-connect
    `disconnect` is a deliberate no-op and `pair` is idempotent, so the shared connect/disconnect
    flow resolves correctly on Windows. Needs an on-device run to confirm vs the spikes.
- `tests/AutoSwitchKVM.Core.Tests/` — xUnit; `ConfigTests.cs` + `Fakes/Fake{Usb,Bluetooth}*`.

CI: a `windows-latest` job in `.github/workflows/ci.yml` runs `dotnet test` on the Core tests.

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
- **disconnect (= unpair)** = `Pairing.UnpairAsync()`. ~3s typical.
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
- **`PairAsync` needs the discovered endpoint, custom ConfirmOnly, and a real callback.** Basic
  `Pairing.PairAsync()` instant-fails this device. A PowerShell **scriptblock** `PairingRequested`
  handler deadlocks (single-threaded runspace) → `RejectedByHandler`; the spike uses a compiled
  delegate. In the C# app this is a non-issue (the callback runs on a threadpool thread).
- **Pair is slow (~14s)** — set the per-call timeout generously (Core default `BtCallTimeoutSecs=30`,
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
- **Tray menu: use `ContextMenuMode.PopupMenu`.** The XAML `SecondWindow` flyout renders as a blank
  rectangle (the secondary window doesn't inherit the app theme); the native popup renders reliably.
- **Size the Settings window** via `AppWindow.Resize` - a WinUI `Window` opens very large by default.

## When making changes

- Touching the engine, config, or models → update/extend `tests/AutoSwitchKVM.Core.Tests`.
- Adding a config field → rely on property initializers for default-on-missing; keep the JSON key in
  sync with the macOS model. Add the absent-vs-null nuance when porting the full `ConfigStore` (M2).
- Follow `PLAN.md` milestones; update its status markers as milestones land.
