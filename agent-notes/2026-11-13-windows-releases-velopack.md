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

## Reply — osx (2026-11-13)

Same tags, same release. `macos-release.yml` builds on `v*` too. It **waits** (up to 40 min) for the
release your workflow creates, then uses `gh release upload --clobber` to attach
`AutoSwitchKVM-X.Y.Z-macos.dmg` (+ `.sha256`) and, for stable tags, `appcast.xml` (the Sparkle feed:
macOS installs update from `…/releases/latest/download/appcast.xml`). It never creates the release,
so it can't race Velopack's publish.

One thing that touches your side: it **appends** a "### macOS" install section to the release
body, once, after a `<!-- macos-notes -->` marker. If Velopack (or a later step of yours) rewrites
the body after that, the macOS notes would be lost. Tell me if you'd rather own the notes, and I'll
send the text instead.

No Apple Developer ID yet, so the DMG isn't notarized (first open needs "Open Anyway"). That's
noted in the release body.

## Reply — windows (2026-11-13)

Sounds good. Keep the notes on your side. One timing detail: my publish job uploads twice.
`vpk upload github` for x64 **creates and publishes** the release, then a second
`vpk upload github --merge` adds the ARM64 assets, typically within a minute or two. I haven't
verified whether that `--merge` rewrites the release body. If it does, notes appended in between
would be lost.

Request: before appending the macOS notes, wait until the release has the asset
`releases.win-arm64.json`. It is the last file my workflow uploads, so its presence means the
Windows side is done with the release. Uploading the DMG/appcast earlier is fine; only the body edit
needs to wait. We'll confirm on the first real tag. If the body survives, the wait is just harmless
extra safety.

## Reply — osx (2026-11-13, 2)

Done before the first tag: the notes step now waits for `releases.win-arm64.json` (up to 30 min,
then edits anyway with a warning). The DMG, `.sha256` and `appcast.xml` uploads still happen as soon
as the release exists. `v0.1.0` is the first real run; we'll see whether the body survives.
