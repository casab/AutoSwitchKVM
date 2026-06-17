# AutoSwitch KVM

Automatically connect/disconnect Bluetooth devices when a chosen USB "switcher source" appears or
disappears. The use case is a KVM switch that exposes a USB hub to whichever computer is selected:
when this machine is selected, its Bluetooth trackpad/keyboard reconnect; when it's switched away,
they release for the other host.

The goal is the **same functionality as a native app on each platform** — macOS, Windows, and
(later) Linux — each in its own folder, sharing the concept and behavior but with platform-native
code, UI, and Bluetooth/USB APIs.

## Platforms

| Platform | Folder | Status |
|----------|--------|--------|
| macOS    | [`osx/`](osx/) | **Stable** — menu bar app (SwiftUI), profiles, Learn-source, reliable handoff, global shortcuts, diagnostics. See [`osx/README.md`](osx/README.md). |
| Windows  | [`windows/`](windows/) | **In progress** — Bluetooth/USB validated on hardware (Milestone 0); solution scaffold (Core lib + WinUI 3 tray app + tests) in place (Milestone 1). C#/.NET 8 + WinUI 3. See [`windows/README.md`](windows/README.md). |
| Linux    | —      | Future; not a current focus. |

## How it works (shared concept)

1. Watch USB device attach/detach to detect the **source** — a named set of USB device IDs that
   appear when the KVM selects this machine.
2. With a short debounce, when the source **appears** connect the configured Bluetooth devices; when
   it **disappears** disconnect them.
3. Optionally **manage pairing** per device (pair on connect / unpair on disconnect) for devices
   like the Apple Magic Trackpad that only work cleanly with one host at a time.
4. **Profiles** group a source + devices; global options (timing, notifications, shortcuts) are
   app-wide.

Each platform implements this with its native toolkit (e.g. macOS uses IOKit + IOBluetooth). The
full, platform-neutral behavior contract is in [`SPECIFICATION.md`](SPECIFICATION.md).

## Repository layout

```
osx/        macOS app — sources, build files, tests, and macOS-specific docs (PLAN, IMPROVEMENTS, CLAUDE)
windows/    Windows app (planned)
.github/    CI workflows (currently builds/tests the macOS app under osx/)
README.md         This overview
SPECIFICATION.md  Platform-neutral behavior contract (what every port must do)
CLAUDE.md         General guidance for working in this repo
```

Platform-specific notes, design docs, roadmaps, and source live in each platform folder. This root
holds only cross-platform overview and shared goals.
