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
- `windows/` — the Windows app (C#/.NET 8 + WinUI 3). **Running on real hardware, being
  hardened.** Core (models, config, engine, source-learner) is ported and unit-tested. The platform
  layer (USB monitor, WinRT Bluetooth, hotkeys, power, login item, toasts), tray UI and Settings
  are implemented and run on-device. Current focus is handoff reliability: failure classification,
  logging, and pair/release races. Docs: `windows/CLAUDE.md`, `windows/PLAN.md`, `windows/README.md`.
- `SPECIFICATION.md` — the **platform-neutral behavior contract** (config model, engine state
  machine, Bluetooth controller operations). Both engines implement it; sections that only one
  platform implements so far are marked (e.g. "Windows implemented; macOS pending").
- `testdata/config-golden.json` — shared config fixture; both test suites read it, so a key
  rename on either side fails CI.
- `CHANGELOG.md` — user-facing changes per release, fed into the GitHub release notes. **When you
  commit something users would notice, add a line under `## Unreleased`** (Both / macOS /
  Windows). The agent cutting a release renames it to `## vX.Y.Z (date)` before tagging; the
  rules are at the top of the file.
- `agent-notes/` — **cross-agent handoff** (see below).
- `website/` — autoswitchkvm.com (Eleventy, Cloudflare Workers static assets; see `website/README.md`). Shared by
  both platforms: it shows the screenshots from `osx/docs/images/` and `windows/docs/images/`.
- `.github/` — CI: macOS job (`swift build`/`swift test` + blocking lint) and a Windows job
  (`dotnet test` on `AutoSwitchKVM.Core.Tests` + WinUI app build).
  `release.yml`: a `v*` tag builds both apps (Windows Velopack packages, macOS DMG + Sparkle
  appcast) into one GitHub release, with notes from `CHANGELOG.md`. It replaced the separate
  `windows-release.yml` + `macos-release.yml` on 2026-11-13. One release per version.

## Where to work

- **Working on macOS?** Read `osx/CLAUDE.md` first: build/test commands, architecture,
  conventions, and gotchas. Everything macOS lives under `osx/`.
- **Working on Windows?** Read `windows/CLAUDE.md` first: build/run, solution layout, Milestone 0
  findings, gotchas. `windows/PLAN.md` has the milestone roadmap. Keep all Windows code/docs under
  `windows/`.
- Keep this root limited to **cross-platform** overview and shared concepts. Platform-specific
  build details and source belong in the platform folder.

## Working alongside the other platform's agent

Two agents usually work in parallel, one on Windows and one on macOS, typically on different
machines. They coordinate through **`agent-notes/`** (protocol in `agent-notes/README.md`):

- **At session start:** `git pull --ff-only` and check for open notes addressed to you
  (`to: osx|windows|all`). The one-liner is in `agent-notes/README.md`. Read them before related work.
- **Write a note** when you change the shared contract (`SPECIFICATION.md`, config keys, golden
  fixture), see hardware behavior that matters to the other side (device quirks, timings, the
  other host's release behavior), fix a bug whose pattern likely exists in the other codebase, or
  need the other side to measure/verify/port something.
- **Reply** by appending a section to the note and updating its `status:`. Never rewrite the
  other agent's text.
- Notes travel through git. Agents commit and push only when the user asks, so when you have
  written a note, **tell the user it is waiting to be committed/pushed**.
- Settled findings move into `SPECIFICATION.md` or a platform `CLAUDE.md`, and the note is
  marked `done`.
- Don't edit the other platform's source or `CLAUDE.md` as part of your own work. Ask through a
  note instead (the user may still direct cross-platform edits explicitly).

## Shared concept (must stay consistent across platforms)

Watch USB attach/detach → detect the named **source** (a set of USB device IDs) → debounced, on
source-present connect the configured Bluetooth devices, on source-absent disconnect them →
optional per-device pair-on-connect / unpair-on-disconnect → **profiles** group a source + devices,
with app-wide global options. When porting to a new platform, mirror this behavior and the
settings model.

Hardware facts both platforms depend on (Apple Magic Trackpad, Classic HID):

- **The bond is exclusive.** The handoff is pair-on-arrive / unpair-on-leave. The receiving host
  cannot pair until the releasing host has unpaired. Until then it sees refusals or no answer
  (seen from Windows as `AuthenticationNotAllowed` for about 20 s after a switch). A slow release on
  one side is felt as a slow connect on the other.
- **The release can land after the other host has already re-paired.** Both engines self-heal
  (stale-bond re-pair on retry, auto-reconnect on drop). The planned structural fix is a LAN
  release-ack handshake between the two apps.
- **Failure classification** (`SPECIFICATION.md` §3): busy/refused failures retry soon without
  spending the retry budget; only possible-stale-bond failures trigger a re-pair from scratch.
