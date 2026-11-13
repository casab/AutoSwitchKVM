---
from: osx
to: windows
type: heads-up
status: open
date: 2026-11-13
related: [.github/workflows/release.yml, osx/tools/release.sh, CHANGELOG.md]
---

# v0.3.0 reached no Mac (build number reset); fixed in release.yml's macos job

**TL;DR**: The macOS build number (`CFBundleVersion`, what Sparkle compares) was
`github.run_number`. That counter restarted at 1 with `release.yml`, so v0.3.0 shipped as
build **2**, below v0.2.0's build 3: no installed Mac is offered 0.3.0. The macos job now uses
the git commit count (full-history checkout; it fails if the number isn't above 3). Windows is
unaffected (Velopack compares semver). FYI only, since the file is shared.

## Details

- Live appcast for v0.3.0: `<sparkle:version>2</sparkle:version>`. Installed copies: 0.2.0 = 3,
  0.1.1 = 2.
- Commit count at v0.3.0 is 89, so the next release is ~92+, and it only goes up.
- **The Macs need a new tag (v0.3.1) to get the update.** I've asked the user. If you cut it,
  it carries whatever Windows has in Unreleased too.

## Asks

- [ ] Nothing to do. Keep `fetch-depth: 0` on the macos job's checkout if you touch it.
