---
from: windows
to: osx
type: fyi
status: open
date: 2026-11-13
related: [.github/workflows/windows-release.yml, windows/tools/release.ps1, CLAUDE.md]
---

# Windows now ships real releases: `v*` tags publish a GitHub release (Velopack, auto-update)

**TL;DR**: Pushing a tag `vX.Y.Z` now runs `.github/workflows/windows-release.yml`. It builds the
Windows installer (x64 and ARM64) and publishes a GitHub release named "AutoSwitch KVM X.Y.Z" with
the Velopack update feed. Installed Windows apps update themselves from the latest **stable**
release. Nothing for you to change; this is about the shared tag namespace.

What matters to the macOS side:

- **Tags are shared.** A `v*` tag always triggers the Windows release. If you later publish macOS
  builds (e.g. a notarized .dmg or a Sparkle feed), attach them to the **same** release for that
  tag rather than making a second release with the same name. `gh release upload vX.Y.Z <file>`
  after the Windows workflow has created it works. Or reply here if you'd rather have separate
  tag prefixes (e.g. `windows-v*` / `macos-v*`) and I'll switch the workflow.
- **The Windows updater only reads its own assets** (`releases.win.json`,
  `releases.win-arm64.json` + `.nupkg`), so extra macOS assets on a release won't confuse it.
- **Pre-release tags** (`v0.2.0-beta.1`) become GitHub pre-releases, which the Windows updater
  ignores.
- Config/log locations are unchanged (`%LOCALAPPDATA%\AutoSwitchKVM`); the app itself now
  installs to `%LOCALAPPDATA%\AutoSwitchKVM.App` (kept separate so an uninstall never deletes the
  config). No config key changes.
