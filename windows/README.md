# AutoSwitch KVM — Windows

Native Windows port of AutoSwitch KVM. **Status: Milestone 0 done (Bluetooth/USB validated), Milestone 1 scaffold in place.**

The goal is feature parity with the macOS app (`../osx/`): a background **system-tray** app that
detects a USB "switcher source" appearing/disappearing and pairs/connects (or unpairs/disconnects)
the configured Bluetooth devices, with the same model (profiles, per-device pairing management,
timing, global shortcuts).

## Working reference: `reference/Trackpad-AutoSwitch.ps1`

A proven PowerShell prototype (the Windows counterpart of macOS's `hammerspoon_init.lua`) that does
the full handoff for an Apple Magic Trackpad on Windows 11. The native port should mirror its
behavior. Key things it establishes:

- **Source detection (USB / PnP).** Watches the KVM hub `USB\VID_05E3&PID_0626` (Genesys Logic
  SuperSpeed hub) as the selection signal — it reliably goes from `Status=OK` to absent when the KVM
  switches away. The sibling `PID_0610` hub has an always-on instance and **can't** be used to
  detect switch-away. It "learns" the instance group key to scope to the right physical hub.
- **No polling.** Event-driven via WMI `Win32_DeviceChangeEvent` + `Win32_SystemConfigurationChangeEvent`
  and a Kernel-PnP EventLog watcher, **plus a ~2 s periodic reconcile** safety-net (the USB
  re-enumeration storm on a KVM switch starves a pure debounce, so a steady reconcile guarantees
  convergence). Debounce ≈ 1200 ms. The reconcile is idempotent (acts only on a transition).
- **Handoff = pair/unpair, not connect/disconnect.** The Magic Trackpad is **Classic HID**
  (`BTHENUM\…`, HID service GUID `00001124-0000-1000-8000-00805F9B34FB`), not BLE. On **select**:
  pair, then verify the HID PnP nodes appear. On **deselect**: **unpair / remove the bond** — because
  the device keeps one link key per host, and once the Mac re-pairs, Windows's stored key is stale;
  you must re-pair on return. (This is exactly the macOS `managePairing` model.)
- **Pair-release race.** Right after a Mac→Windows switch the Mac may still hold the device, so
  pairing returns `AuthenticationFailure`; the prototype retries across successive reconciles rather
  than giving up.
- **Pairing tooling.** Uses the PolarGoose `BluetoothDevicePairing.exe` CLI
  (`pair-by-mac` / `unpair-by-mac`), with `pnputil /remove-device` as a last-resort unpair. A native
  app could use WinRT `DeviceInformation` pairing instead, or bundle a helper.

## Build plan

See **`PLAN.md`** for the full build plan — stack (C#/.NET 8 + WinUI 3, Windows 10 1809+/11,
unpackaged, native WinRT pairing), solution structure, a component-by-component mapping from the
macOS app, the platform discrepancies and how they're resolved, and milestones (spike-first). The
behavior contract it targets is `../SPECIFICATION.md`.

## Solution layout (Milestone 1 scaffold)

```
AutoSwitchKVM.sln
src/
  AutoSwitchKVM.Core/      net8.0 class library (portable, unit-tested)
    Models/Models.cs        USBSource, BTDevice, Profile, KeyShortcut, AppConfig (System.Text.Json)
    ConfigStore.cs          %LOCALAPPDATA%\AutoSwitchKVM\config.json load/save (camelCase parity w/ macOS)
    IUsbMonitor.cs          USB attach/detach interface
    IBluetoothController.cs  Bluetooth control surface (encodes the Milestone 0 recipe in doc comments)
  AutoSwitchKVM.App/       net8.0-windows10.0.19041.0, WinUI 3, unpackaged (WindowsPackageType=None)
    App.xaml(.cs)           tray-only app (H.NotifyIcon) - context menu Open Settings / Exit
    SettingsWindow.xaml(.cs) empty window with the 5 tabs (Source/Devices/General/Extras/Diagnostics)
    Platform/
      WinRtBluetooth.cs      IBluetoothController stub (bodies = Milestone 4; recipe in comments)
      PnpUsbMonitor.cs       IUsbMonitor stub (bodies = Milestone 3)
tests/
  AutoSwitchKVM.Core.Tests/  xUnit - config tests + Fake USB/Bluetooth for the engine port (M2)
```

## Build & run

Requires **Visual Studio 2022** with the **.NET Desktop** workload and the **Windows App SDK C#
templates** (or the standalone Windows App SDK), and the **.NET 8 SDK**.

- Open `AutoSwitchKVM.sln`, set **AutoSwitchKVM.App** as startup, platform **x64**, and run. You
  should get a tray icon; click it (or right-click → Open Settings) to open the empty Settings window.
- Tests: `dotnet test tests/AutoSwitchKVM.Core.Tests` (the Core lib + tests are plain `net8.0`, no
  Windows SDK needed).

Notes:
- The app is **unpackaged**; the Windows App SDK bootstrapper auto-initializes (`WindowsPackageType=None`).
- NuGet versions in the `.csproj` files are a known-good baseline — if restore complains, let VS
  update `Microsoft.WindowsAppSDK` / `H.NotifyIcon.WinUI` to the latest. The tray creation in
  `App.xaml.cs` follows H.NotifyIcon 2.x; adjust if you bump to a different major version.
- Everything under `src`/`tests` was authored on a Mac and **not yet compiled on Windows** — expect
  to smooth over minor build hiccups on first open.

## Approach summary

- **Language/UI:** C# / .NET with a tray icon (WinUI 3, WPF, or Win32 `NotifyIcon`).
- **USB detection:** `CM_Register_Notification` / `WM_DEVICECHANGE` (or the WMI/Kernel-PnP sources
  the prototype uses), watching the source's vendor/product IDs; keep the PID_0626-as-signal nuance
  and the periodic-reconcile safety-net.
- **Bluetooth:** pairing via WinRT `DeviceInformation.Pairing` (`Pair`/`Unpair`) or a bundled CLI;
  verify connection via the HID PnP nodes for the device's MAC.
- **Config:** mirror the macOS `AppConfig`/`Profile` JSON shape so settings concepts (and ideally
  exported files) line up across platforms.

## Reference

- Behavior contract / shared concept: `../README.md` and `../CLAUDE.md`.
- macOS implementation (reference for engine behavior): `../osx/`.
- Windows prototype: `reference/Trackpad-AutoSwitch.ps1`.
