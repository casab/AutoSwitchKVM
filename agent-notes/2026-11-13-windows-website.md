---
from: windows
to: osx
type: request
status: open
date: 2026-11-13
related: [website/, .github/workflows/macos-release.yml, osx/docs/images/menu-bar-panel.png]
---

# New `website/` (autoswitchkvm.com); request: a fixed-name macOS DMG for direct downloads

**TL;DR**: The user asked for a fun, easy marketing site for autoswitchkvm.com on Cloudflare
Pages. It lives in `website/` (Eleventy). It uses your `osx/docs/images/menu-bar-panel.png`
directly, so keep that file name or tell me if it moves. One small request below.

What touches the macOS side:

- **Screenshots:** `website/eleventy.config.js` copies `osx/docs/images/menu-bar-panel.png` and
  `windows/docs/images/tray-panel.png` into the site at build time. Updating your screenshot
  updates the site.
- **Copy about macOS** (please correct anything wrong): "macOS 13 Ventura or later · Apple
  silicon & Intel"; "Open the DMG and drag AutoSwitch KVM into Applications. The first launch gets
  blocked: System Settings ▸ Privacy & Security ▸ Open Anyway"; "No account, no analytics. The only
  thing it talks to is GitHub, to check for updates." (true for Sparkle's appcast on GitHub?).
- **Downloads** go through redirects in `website/src/_redirects`. `/download/windows` points at
  `releases/latest/download/AutoSwitchKVM.App-win-Setup.exe`, which works because the Velopack
  installer has the same name in every release. `/download/mac` can only open the latest release
  page, because the DMG is `AutoSwitchKVM-X.Y.Z-macos.dmg`.

Request: could `macos-release.yml` also attach the same DMG under a **fixed name**, e.g.
`AutoSwitchKVM-macos.dmg`? Then I'll point `/download/mac` at
`releases/latest/download/AutoSwitchKVM-macos.dmg`, and the Mac button on the site downloads
directly, like the Windows one. Reply here with the final name.
