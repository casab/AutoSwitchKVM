# AutoSwitch KVM — Windows Build Plan

A native Windows port with feature parity to the macOS app. The behavior contract is
`../SPECIFICATION.md`; the proven reference is `reference/Trackpad-AutoSwitch.ps1`; the macOS code
in `../osx/` is the reference engine implementation.

## Decisions (confirmed)

| Area | Choice |
|------|--------|
| Language / UI | **C# / .NET 8 + WinUI 3** |
| Bluetooth pairing | **Native WinRT** (`Windows.Devices.Enumeration` `DeviceInformation.Pairing`) |
| Min OS | **Windows 10 1809+ and 11** (use Win11-only APIs only if nothing equivalent on 10) |
| Distribution | **Unpackaged, self-contained**, installed and auto-updated by **Velopack** from GitHub Releases + run-at-login |

## Solution structure

Split into a portable core + a thin platform app (the split macOS skipped, but worth doing here for
testability):

```
windows/
  AutoSwitchKVM.sln
  src/
    AutoSwitchKVM.Core/      net8.0 class library — NO WinUI/WinRT-UI deps
      Models/                USBSource, BTDevice, Profile, KeyShortcut, AppConfig (System.Text.Json)
      ConfigStore.cs         %LOCALAPPDATA%\AutoSwitchKVM\config.json (atomic, debounced, back-compat)
      SelectionEngine.cs     port of the macOS engine (presence, debounce, connect/disconnect, backoff)
      IUsbMonitor.cs         interface (snapshot + add/remove events)
      IBluetoothController.cs interface (§5 of the spec)
      SourceLearner.cs
    AutoSwitchKVM.App/       net8.0-windows10.0.x WinUI 3 app (unpackaged)
      Platform/
        PnpUsbMonitor.cs     IUsbMonitor via device-change notifications + reconcile
        WinRtBluetooth.cs    IBluetoothController via WinRT pairing + connection detection
        HotKeyService.cs     RegisterHotKey + hidden message window
        ToastNotifier.cs     Windows toasts (AppUserModelID registration)
        PowerMonitor.cs      SystemEvents.PowerModeChanged
        LoginItem.cs         HKCU Run key
        DebugLog.cs
      UI/                    Tray icon + flyout panel, Settings window (tabs), recorders
  tests/
    AutoSwitchKVM.Core.Tests/  xUnit — engine/config/models with fake USB + BT (mirror osx tests)
```

CI: **added** - a `windows-latest` job in `.github/workflows/ci.yml` runs `dotnet test` on
`AutoSwitchKVM.Core.Tests` and builds `AutoSwitchKVM.App` (alongside the macOS swift build/test +
blocking lint jobs). Runtime Bluetooth/USB behavior is still validated on-device.

## Component mapping (macOS → Windows)

| macOS (`osx/`) | Windows | Notes |
|----------------|---------|-------|
| `SelectionEngine` (Swift) | `SelectionEngine` (C#) | Port logic ~1:1; `async`/`await`; marshal to UI thread via `DispatcherQueue` |
| `Models` + `ConfigStore` | same, `System.Text.Json` | Keep identical JSON keys; custom converters for default-on-missing + legacy migration |
| `USBMonitor` (IOKit) | `PnpUsbMonitor` | `Windows.Devices.Enumeration.DeviceWatcher` (USB selector) or CfgMgr32 `CM_Register_Notification`; parse `USB\VID_xxxx&PID_xxxx`; + ~2 s reconcile timer |
| `IOBluetoothController` | `WinRtBluetooth` | WinRT custom pairing + Win32 `BluetoothRemoveDevice` for release; see discrepancies |
| `HotKeyManager` (Carbon) | `HotKeyService` | Win32 `RegisterHotKey` + hidden `HWND` `WM_HOTKEY` loop |
| `Notifier` (UNUserNotification) | `ToastNotifier` | `Microsoft.Windows.AppNotifications`; `Register()` handles unpackaged COM registration, but toast display still needs on-device verification |
| `SleepWakeMonitor` (NSWorkspace) | `PowerMonitor` | `Microsoft.Win32.SystemEvents.PowerModeChanged` (Suspend/Resume) |
| `LoginItem` (SMAppService) | `LoginItem` | `HKCU\…\Run` value (or a logon scheduled task) |
| `DockManager` (Extra) | — | macOS-only; Windows Extras tab empty for now |
| `MenuContentView` + tray icon | Tray icon (`H.NotifyIcon` or Shell_NotifyIcon) + WinUI flyout | WinUI 3 has no built-in tray |
| `SettingsView` tabs | WinUI Settings window | Same tabs/controls |
| `Log` + `DebugLog` | `DebugLog` | Categorized file log + in-app buffer |

## Platform discrepancies & resolutions

1. **Connect/disconnect vs pair/unpair (biggest).** For Classic-HID devices (Magic Trackpad),
   Windows connects the HID **on pairing** and there is no separate "connect" call; "disconnect"
   means **unpair** (remove bond). Resolution: in `WinRtBluetooth`, map `connect` ≈ ensure paired
   (HID auto-connects), `disconnect` ≈ unpair, `isConnected` ≈ HID PnP nodes present (or
   `BluetoothDevice.ConnectionStatus == Connected`). Practically, **`managePairing` is the norm on
   Windows** for these devices; surface that default sensibly.
2. **Stale link key on handoff.** A device holds one bond per host; once the Mac re-pairs, Windows's
   stored key is stale → must unpair on leave and re-pair on return (already the spec's
   `managePairing` flow; the prototype confirms it).
3. **Pair-release race.** Right after a Mac→Windows switch the device may still be held by the Mac;
   pairing returns an auth failure. The engine's bounded retry + backoff already handles this
   (prototype retried across reconciles).
4. **Source signal nuance.** The KVM hub exposes an always-on instance (`PID_0610`) plus one that
   disappears on switch-away (`PID_0626`). Learn should prefer the disappearing PID as the signal;
   the engine treats presence of any configured ID as selected.
5. **No polling.** Use `DeviceWatcher`/CfgMgr32 notifications, but keep a ~2 s reconcile timer — the
   KVM's USB re-enumeration storm starves a pure debounce (prototype lesson).
6. **Pairing ceremony.** Use custom pairing (`DeviceInformation.Pairing.Custom`) and auto-handle the
   ceremony (`ConfirmOnly` / `ProvidePin` empty) — analogous to the macOS auto-confirm.
7. **Adapter power.** `Windows.Devices.Radios.Radio` (Bluetooth radio `State`), not a connect call.
8. **Global hotkeys differ.** Defaults can't be ⌃⌥⌘ (no Command on Windows). Use e.g.
   `Ctrl+Alt+P/C/D`; the `KeyShortcut` model carries Windows modifier flags + display string.
   `RegisterHotKey` needs a message-pump window; avoid OS-reserved combos.
9. **Toasts when unpackaged.** Use Windows App SDK `AppNotificationManager.Register()` so the
   calling process is registered as the unpackaged COM server. Verify display on-device; app
   notifications are not supported for elevated/admin runs.
10. **Addresses.** Windows uses a 64-bit Bluetooth address; store the MAC string in config and
    convert at the API boundary.
11. **Config location.** `%LOCALAPPDATA%\AutoSwitchKVM\config.json` (vs macOS Application Support).

## Milestones

0. **Spike (do first — highest risk).** Against the real Magic Trackpad: WinRT custom **pair** +
   **unpair**, **connection detection** (ConnectionStatus / HID nodes), and **radio power**; confirm
   the pair=connect / unpair=disconnect model and the `PID_0626` source signal. Settle the
   `IBluetoothController` shape here.
1. **Scaffold. [DONE - Windows build verified]** `.sln` with Core lib (models + ConfigStore +
   USB/BT interfaces), WinUI 3 unpackaged app (H.NotifyIcon tray + Settings), platform services
   (WinRtBluetooth/PnpUsbMonitor), and xUnit project (config tests + fakes). `Release|x64` app
   build verified on Windows on 2026-06-25; runtime hardware validation remains separate.
2. **Core: models + config + engine. [DONE]**
   `Models` and a basic `ConfigStore` landed in M1. `SelectionEngine` is now ported 1:1 (presence +
   automationActed latch, arrival/departure debounce, runToken cancellation, busyDevices,
   clean-connect-only + managePairing pair->drop->settle->connect, capped exponential backoff,
   bluetooth-off gate, unexpected-disconnect, manual connect/disconnect-all, snapshot + incremental
   USB inputs) with an injectable `SleepHook` for deterministic tests. `SelectionEngineTests` mirrors
   the macOS suite (16 cases) using `Fake{Usb,Bluetooth}`. `ConfigStore` now also does **legacy
   single-source migration** (top-level source/devices -> a "Default" profile), covered by
   `ProfilesTests`. Full unit coverage for M1-M3 lives in `ModelsTests`, `ConfigTests`,
   `ProfilesTests`, `SelectionEngineTests`, `SourceLearnerTests` (40 tests). `ConfigStore` now writes
   atomically, exposes debounced saves for UI edits, migrates legacy single-source configs, and
   preserves the absent-vs-present-null hotkey distinction.
3. **USB monitor + Learn source. [DONE - PnpUsbMonitor + SourceLearner authored; on-device wiring at M6]**
   Spike 3 (`spikes/Spike3-Usb.ps1`) validated detection on the real KVM: VID_05E3 PID_0626 toggles
   with the switch while PID_0610 stays; `Win32_DeviceChangeEvent` fires reliably (~113 events / 60s
   during switches - confirming the reconcile safety-net is needed). `PnpUsbMonitor` (App/Platform)
   implements it via `System.Management`: Win32_PnPEntity enumeration + Win32_DeviceChangeEvent
   wakeups (debounced) + ~2s reconcile, emitting a snapshot only on VID/PID-set change, marshaled to
   the captured SynchronizationContext. `SourceLearner` is ported to Core (snapshot-diff, tested with
   FakeUsbMonitor).
4. **Bluetooth controller. [DONE - pending on-device runtime run]** `WinRtBluetooth` implements the
   contract per the Milestone 0 recipe: radio power (`Radio.State`), `isConnected` (ConnectionStatus +
   BTHENUM HID node), idempotent `pair` (`FromBluetoothAddressAsync` ->
   `Custom.PairAsync(ConfirmOnly|...)` auto-accept), `unpair` (Win32 `BluetoothRemoveDevice` first,
   WinRT `UnpairAsync` fallback), `connect` = ensure-paired, `disconnect` = no-op for Classic HID
   (release is unpair), paired-list, and `ConnectionStatusChanged` monitoring marshaled to the
   captured SynchronizationContext. Needs the trackpad/KVM runtime pass to confirm the translation
   matches the spikes.
5. **UI parity. [DONE - builds, pending on-device runtime run]** `AppController` coordinator wires
   config + engine + USB monitor + Bluetooth controller (load, start, seed, periodic poll marshaled
   via DispatcherQueue) and exposes state + actions; `DebugLog` buffer added. The tray uses a
   **dynamic context menu** rebuilt from live state (status header, profile radio items, device rows
   with status that toggle connect/disconnect, Connect/Disconnect all, Pause, Settings, Exit) -
   chosen over a custom borderless flyout window (finicky and untestable blind). `SettingsWindow`
   has the five tabs built in code-behind: Source (display + manual edit + Learn), Devices
   (enable/required-managePairing/delay/reorder/delete/test/add-from-paired), General (timing +
   behavior + shortcut recorder + import/export), Extras (note), Diagnostics (live USB/BT state +
   debug log), plus a profile bar for switch/add/rename/delete. Build verified; hardware behavior
   still needs the on-device runtime pass.
6. **System integration. [DONE - builds, pending on-device runtime run]** `LoginItem` (HKCU Run key),
   `PowerMonitor` (SystemEvents.PowerModeChanged: suspend->disconnect all, resume->reconcile,
   marshaled to UI), `HotKeyService` (Win32 RegisterHotKey on a message-only window + WndProc;
   defaults Ctrl+Alt+P/C/D), `ToastNotifier` (AppNotificationManager unpackaged Register). All wired
   into AppController; the shortcut recorder is in the General tab (ContentDialog + InputKeyboardSource
   modifier read). **Caveat:** app notifications are registered through Windows App SDK
   `AppNotificationManager.Register()` for unpackaged apps, but toast display still needs on-device
   verification (and will not show for elevated/admin runs).
7. **Diagnostics + logging. [DONE]** `DebugLog` buffer + the Diagnostics tab (live USB/BT state,
   per-device status, debug log with copy/clear) landed with Milestone 5.
8. **Distribution. [DONE, unsigned]** Velopack (2026-11-13): self-contained publish (.NET + Windows
   App SDK bundled), per-user installer, background updates from GitHub Releases (Settings ▸ General
   ▸ About, tray menu "Restart to update", a notification). x64 channel `win`, ARM64 channel
   `win-arm64` (built, not tested on hardware). `tools/release.ps1` + `.github/workflows/windows-release.yml`
   on a `v*` tag. Still to do: code signing (SmartScreen warns until then). Size and startup:
   see "Distribution: size and startup" below.

## Distribution: size and startup

Measured 2026-11-13 (x64, self-contained Release publish; startup = launch to visible Settings
window in preview mode, optimized build, median of 5 warm runs, plus the first cold run):

| Build | On disk | Setup.exe | Full .nupkg | Startup warm / cold |
|---|---|---|---|---|
| First release setup (plain Release) | 212.8 MB | 99.4 MB | 91.9 MB | 544 / 1425 ms |
| **Current**: ReadyToRun + AI/ML/Widgets excluded | 212.4 MB | 89.9 MB | 82.7 MB | 419 / 1180 ms |
| Exclusions only, no ReadyToRun (not shipped) | 164.7 MB | n/a | n/a | n/a |

Working set with Settings open: ~160 MB. ARM64 Setup.exe: 94.2 to 83.4 MB.

**What is on (csproj):**
- `PublishReadyToRun`: precompiled IL, about 23 % faster startup for about 48 MB on disk (about
  15 MB compressed).
- `ExcludeAssets="all"` on `Microsoft.WindowsAppSDK.AI`, `.ML`, `Microsoft.Windows.AI.MachineLearning`,
  `.Widgets`: the metapackage pulls them in (onnxruntime.dll + DirectML.dll + Windows AI/Widgets,
  60 files, about 49 MB) and nothing here uses them. Keep their versions in step with the
  metapackage when bumping it. If a future feature needs one of them, remove its exclusion.
- Velopack delta updates: after the first install, updates download only the changed bytes.

**Future options, roughly in order of value / risk:**
1. **Drop non-English WinUI `.mui` files** (88 language folders, about 3.5 MB on disk, ~1 MB
   compressed). `SatelliteResourceLanguages` does not touch them (native files); it needs a
   publish target that removes `ResolvedFileToPublish` items with `.mui` outside `en-*`. Small gain.
   Risk: the control-string fallback on a non-English Windows display language must be tested first.
2. **Partial / composite ReadyToRun**: precompile only the assemblies on the startup path
   (`PublishReadyToRunExclude` for big, rarely used ones such as parts of
   `Microsoft.Windows.SDK.NET.dll`), or try `PublishReadyToRunComposite`. Measure both size and
   startup; the gain over the current setup is uncertain.
3. **Trimming (`PublishTrimmed`)**: the biggest size win (the 24 MB `Microsoft.Windows.SDK.NET.dll`
   projection and most of the BCL are mostly unused). Risky: WinUI/CsWinRT interop, x:Bind's
   generated code, CommunityToolkit and H.NotifyIcon use reflection, and trim breaks only show at
   runtime (missing type or member). Needs `TrimMode=partial` first, all trim warnings fixed, and a
   full on-device pass (tray, Settings, every dialog, toasts, hotkeys, Bluetooth).
4. **Native AOT**: smallest and fastest start, lowest memory. WinUI 3 supports it (Windows App SDK
   1.6+), but `System.Management` (WMI in `PnpUsbMonitor`) is not AOT-compatible and would have to be
   rewritten on CfgMgr32 / `SetupDi` P/Invoke or WinRT `DeviceWatcher`. H.NotifyIcon and the
   CommunityToolkit controls need AOT checks too. A larger project; consider it with trimming,
   since AOT implies full trimming.
5. **Framework-dependent install**: rely on a shared .NET 8 Desktop runtime and/or the Windows App
   SDK runtime instead of bundling them (much smaller package). Velopack can install some
   prerequisites at setup (`vpk pack --framework`); check whether it covers the Windows App SDK
   runtime. Trade-off: an extra install step and a runtime the app doesn't control.
6. **Idle memory**: a tray app sits idle most of the day. Measure the working set with no windows
   open after a few hours. If high, release the Settings window and its pages on close.

## Planned: Logitech receivers (Unifying / Bolt)

Recorded 2026-11-13; not scheduled (Bluetooth channel switching, spec §5a type 5, comes first and
needs the Mac side to work both ways).

- **Receiver on the KVM's USB hub: nothing to build.** The KVM switches it like a wired device, at
  receiver latency. Documented as the recommended setup (READMEs, website `/devices/`): use the
  KVM's plain hub port, not an emulated keyboard/mouse port.
- **A receiver in each computer (or receiver on one, Bluetooth on the other):** switching is the
  same HID++ 0x1814 Change Host, sent through the receiver's HID++ interface (Bolt PID `C548`,
  Unifying `C52B`) with the device's receiver slot (1-6) as the device index instead of `0xFF`.
  `reference_flow/` does exactly this. The work is the **device identity**: a device behind a
  receiver has no Bluetooth address, and `BTDevice.address` keys everything (config, monitoring,
  status). It needs a second identity (receiver + slot, or the device's unit ID / wireless PID), a
  picker from the receiver's paired list, and connection status from the receiver's notifications.
  That is a config-model change, so agree on it with macOS first (agent-note + spec §2).
- Mixed setup today: receiver on the PC, Bluetooth on the Mac. Mac → PC already works (the Mac
  sends Change Host over Bluetooth); PC → Mac needs the receiver path.
- Lightspeed (G series) devices are mostly single-host: the KVM-hub setup is the answer.

## Milestone 0 — spike findings

**Spike 1 (read-state, `spikes/Spike1-ReadState.ps1`) — DONE, validated on the real Magic Trackpad
(Win 11, PowerShell 5.1).** Two runs: trackpad active-on-Windows, then **after removing it from
Windows Bluetooth so the Mac could take it** (see "exclusive bond" below):

| Signal | Active on Windows | After removal (on Mac) | Conclusion |
|--------|-------------------|------------------------|------------|
| `Radio.State` | `On` | `On` | `isPoweredOn()` = radio state. |
| `BluetoothDevice.ConnectionStatus` | `Connected` | `Disconnected` | **Primary `isConnected()` signal** — Connected only when bonded AND in use. |
| `BTHENUM\…DEV_<mac>` HID PnP node | present, `Status=OK` | absent | Corroborating `isConnected()` fallback; in lockstep with ConnectionStatus. |
| `FromBluetoothAddress…Pairing.IsPaired` | `True` | `False` | `False` reflects the **manual unpair**, not an automatic loss — see exclusive-bond note. |
| MAC formatting | `3C:50:02:BF:22:45` matches config | same | Address handling OK. |

**Exclusive bond — the defining constraint (corrected after device-owner feedback).** The bond is
*exclusive*: **while Windows holds the pairing, the Mac cannot connect at all.** Engin confirmed he
must *remove* the trackpad from Windows Bluetooth before the Mac will take it. So the `IsPaired=False`
in run 2 was that manual removal — Windows does **not** silently lose the bond. Consequences:
- The handoff is **not passive**. The side releasing the device must **actively unpair** (remove the
  bond) on switch-away; "disconnect" is not enough and Windows exposes no real disconnect for a
  Classic-HID device anyway. So on Windows, `disconnect` ≡ **unpair**, `connect` ≡ **pair**.
- Symmetric: to bring the trackpad *to* Windows, it must first be free of the Mac (Mac releases it).
  The macOS app's `managePairing` is the Mac-side half of this.
- `managePairing` is therefore **mandatory, not optional**, for this device class on Windows.

Implementation notes:
- Use `FromBluetoothAddressAsync(addr).DeviceInformation.Pairing.IsPaired` as the authoritative
  paired check. The `GetDeviceSelectorFromPairingState($true)` + `FindAllAsync` enumeration reported
  `IsPaired=False` even while connected — its pairing prop is unreliable; use it only for discovery/MACs.
- `FromBluetoothAddressAsync` returns a non-null device even when switched-away/unpaired (cached
  record), so status queries are always available.
- PowerShell 5.1 reads BOM-less scripts as Windows-1252 — keep spike scripts pure ASCII (an em-dash
  in a live line was misread as a smart-quote delimiter and broke parsing).

**Spike 2 (pairing ceremony, `spikes/Spike2-Pairing.ps1`) — DONE. Native WinRT does the full handoff
headlessly; no PolarGoose CLI needed.** Validated on the real trackpad:

- **Unpair / release** initially validated with `Pairing.UnpairAsync()` (`Unpaired` in ~3.2s,
  afterwards `conn=Disconnected`, `IsPaired=False`, HID node gone). Later diagnostics showed WinRT
  unpair can leave the active radio link attached, so the app now calls Win32 `BluetoothRemoveDevice`
  first and keeps `UnpairAsync` as fallback.
- **Pair** = custom **ConfirmOnly** ceremony, NOT basic pairing. Sequence that works:
  1. Discover the *unpaired* association endpoint: `FindAllAsync(GetDeviceSelectorFromPairingState($false))`,
     match the target by MAC — the endpoint `Id` carries the MAC **with colons**
     (`Bluetooth#Bluetooth<adapter>-3c:50:02:bf:22:45`), so strip separators before matching.
  2. `endpoint.Pairing.Custom.PairAsync(DevicePairingKinds.ConfirmOnly)` with a `PairingRequested`
     handler that calls `args.Accept()`. Result `Paired` in ~13.7s; afterwards `conn=Connected`,
     `IsPaired=True`, HID node `Status=OK`. `handlerKind=ConfirmOnly`.
  - **Do NOT gate on `CanPair`** — the discovered endpoint reported `CanPair=False` yet pairing
    succeeded. Treat `CanPair` as advisory only (like `IsPaired` on the FindAll path).
  - Basic `Pairing.PairAsync()` does NOT work for this device — instant `Failed` (~35ms) because
    it needs the custom ConfirmOnly ceremony.
- **PowerShell-only caveat (not a WinRT limit):** a scriptblock `PairingRequested` handler deadlocks
  PS 5.1's single-threaded runspace (blocking await starves the callback → `RejectedByHandler`). The
  spike works around it with a *compiled* delegate (`Add-Type` `PairAccepter`). In the C# app this is
  a non-issue — the callback runs on a normal threadpool thread.

Engine implications for Windows: **pair is slow (~14s)** — set the per-call timeout generously
(~30s; the macOS default of 15s is borderline). The Mac-release race (pairing fails while the Mac
still holds the bond, `CanPair=False`/not discoverable) is handled by the engine's existing bounded
retry + backoff; re-discover the endpoint on each attempt.

## Testing

The Core library (engine/config/models) is the portable, high-value test target — same approach as
macOS: xUnit with fake USB + Bluetooth, no hardware. Platform services (PnP, WinRT BT, hotkeys) are
validated on-device, like the macOS Bluetooth layer.

## Open questions / risks

- ~~WinUI 3 tray approach (`H.NotifyIcon` vs raw `Shell_NotifyIcon`).~~ **Settled:** H.NotifyIcon native popup menu builds and renders reliably enough for the tray surface.
- Reliability of WinRT custom pairing for Classic-HID across the handoff (the spike answers this; the
  PolarGoose CLI remains a documented fallback behind `IBluetoothController`, mirroring the macOS
  native-with-blueutil-fallback design).
- ~~Whether `DeviceWatcher` reports the KVM hub add/remove reliably~~ **Settled (Spike 3):**
  `Win32_DeviceChangeEvent` (WMI) fires reliably as a wakeup; `PnpUsbMonitor` uses it + a ~2s
  reconcile. CfgMgr32 remains a possible later optimization if WMI enumeration cost matters.
- Unpackaged toast display on Windows 10 vs 11: `AppNotificationManager.Register()` is wired for
  unpackaged COM registration; confirm on-device, especially non-elevated vs elevated runs.
- **USB source detection is VID/PID-set coarse (known limitation).** `selected` = any matching VID/PID
  present; there's no instance-group scoping to a specific physical hub (the PowerShell prototype had
  it). Works as long as the source keys on the *disappearing* PID (e.g. `0626`); but an always-on PID
  in the source, or a second identical hub, would keep it "selected" forever. Follow-up: store the
  hub instance-group key in `USBSource` and match it in `PnpUsbMonitor`/engine.
- **Suspend release is best-effort.** On suspend the app queues an async unpair and returns; Windows
  may suspend before WinRT unpair completes, leaving the bond held (sleep-while-selected). The normal
  KVM switch-away path is unaffected. Needs hardware testing; a SystemEvents power-notification delay
  could help but isn't guaranteed.
- **Pairing ceremony coverage.** `PairAsync` now requests ConfirmOnly|ProvidePin|ConfirmPinMatch|
  DisplayPin (handler auto-accepts ConfirmOnly, supplies "0000" for ProvidePin). Covers Apple
  trackpad/keyboard; devices needing real user PIN/passkey entry are out of scope for now.
