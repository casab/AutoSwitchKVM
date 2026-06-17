# CLAUDE.md

Guidance for Claude working in this repository.

## What this is

**AutoSwitch KVM** is a multi-platform project: a native app for each OS that automatically
connects/disconnects Bluetooth devices when a chosen USB "switcher source" appears or disappears
(a KVM handoff). The aim is feature parity across platforms with platform-native code.

This is a **monorepo organized by platform**:

- `osx/` — the macOS app (SwiftUI, IOKit/IOBluetooth). **Stable.** All macOS source, build files,
  tests, and macOS-specific docs live here, including `osx/CLAUDE.md` (detailed macOS guidance),
  `osx/PLAN.md`, and `osx/IMPROVEMENTS.md`.
- `windows/` — the Windows app (C#/.NET 8 + WinUI 3). **In progress:** Bluetooth/USB validated on
  real hardware (M0); Core (models, config, engine, source-learner) ported and unit-tested; the
  platform layer (USB monitor, WinRT Bluetooth, hotkeys, power, login item, toasts), tray UI, and
  Settings are authored (M1–M7); pending a first on-device build. Windows source, build files, and
  docs live here, including `windows/CLAUDE.md` (detailed Windows guidance), `windows/PLAN.md`, and
  `windows/README.md`.
- `.github/` — CI: macOS job (`swift build`/`swift test` + advisory lint) and a Windows job
  (`dotnet test` on `AutoSwitchKVM.Core.Tests`).

## Where to work

- **Working on macOS?** Read `osx/CLAUDE.md` first — it has the build/test commands, architecture,
  conventions, and gotchas. Everything macOS lives under `osx/`.
- **Working on Windows?** Read `windows/CLAUDE.md` first (build/run, solution layout, Milestone 0
  findings, gotchas); `windows/PLAN.md` has the milestone roadmap. Keep all Windows code/docs under
  `windows/`.
- Keep this root limited to **cross-platform** overview and shared concepts. Don't put
  platform-specific build details or source here — they belong in the platform folder.

## Shared concept (must stay consistent across platforms)

Watch USB attach/detach → detect the named **source** (a set of USB device IDs) → debounced, on
source-present connect the configured Bluetooth devices, on source-absent disconnect them →
optional per-device pair-on-connect / unpair-on-disconnect → **profiles** group a source + devices,
with app-wide global options. When porting to a new platform, mirror this behavior and the
settings model.
