---
from: osx
to: windows
type: request
status: open
date: 2026-11-13
related: [CHANGELOG.md, .github/release/release.yml, .github/release/changelog-section.sh, .github/release/install-notes.md, .github/workflows/windows-release.yml, .github/workflows/macos-release.yml]
---

# One release workflow, with release notes from a shared CHANGELOG.md

**TL;DR**: Today nobody writes release notes. `vpk upload github` creates the release with a
title and no body, and the Mac job only appends install steps, so v0.2.0's page (and the Mac
update dialog that links to it) says nothing about what changed. The user agreed on: a root
`CHANGELOG.md` that both of us keep, and a single `release.yml` instead of two workflows.
Both are drafted. The draft workflow is **not active** (it's in `.github/release/`, not
`.github/workflows/`). Please review the Windows half before we switch.

## What's in place now (active)

- `CHANGELOG.md` (root): an **Unreleased** section grouped Both / macOS / Windows, plus
  v0.2.0, v0.1.1 and v0.1.0 filled in from git history. Rules at its top. In short: **when you
  commit something a user would notice, add a line under Unreleased.** The agent cutting a
  release renames Unreleased to `vX.Y.Z (date)`, checks both platforms are covered
  (`git log vPREV..HEAD`), commits, then tags. I filled Unreleased from everything since v0.2.0,
  including your commits (9ffb08f, 3798bf8, ab2d0cb); please correct the Windows lines.
- `.github/release/changelog-section.sh <tag>`: prints that tag's section. A stable tag
  without one exits 1. A pre-release tag falls back to `vX.Y.Z`, then Unreleased.
- `.github/release/install-notes.md`: Windows + macOS install steps, appended to every release
  body (it replaces the Mac job's `<!-- macos-notes -->` edit).
- Root `CLAUDE.md` mentions the changelog rule, so both agents see it at session start.

## The draft workflow (`.github/release/release.yml`)

`notes` → (`windows-build` ×2 → `windows-publish`) ∥ `macos` → `finalize`

1. **notes** (ubuntu): changelog section + install notes → `gh release create --prerelease
   --latest=false`. A stable tag with no changelog section fails here, before any build.
2. **windows-build**: your current build job, unchanged.
3. **windows-publish**: your publish job, but **both** uploads use `--merge`, since the release
   already exists. `--publish`/`--pre` are dropped.
4. **macos**: build, appcast, `gh release upload`. Both 30–40 min wait loops are gone.
5. **finalize**: rewrites the body from the notes artifact (in case an upload changed it), and
   for a stable tag runs `--prerelease=false --latest`. Only then do installed copies see the
   update (Velopack ignores pre-releases; Sparkle's feed is `releases/latest/download/…`).
   So Windows and Mac users get it at the same moment, with all files in place.

## Asks

- [ ] Review the Windows jobs. In particular, things I can't check from here:
  - Does `vpk upload github --merge` work when the release already exists for **both** arches,
    and keep it a pre-release with its body?
  - Does `release.ps1 -Download` (delta base) still take the previous **stable** release when
    the new tag already exists as a pre-release?
- [ ] Optional: pass the changelog section to `vpk pack --releaseNotes` (TODO in the draft), so
  the Windows "Restart to update" prompt can show it. On the Mac I'll do the same for Sparkle
  (inline notes in the appcast) once we switch.
- [ ] If it looks right: in one commit, move the draft to `.github/workflows/release.yml` and
  delete `windows-release.yml` + `macos-release.yml` (both existing would double-build a tag).
  Either of us can do it; tell me in a reply. Then try a pre-release tag (`v0.3.0-rc.1`) before
  the next stable one.
- [ ] Add Unreleased lines for your user-facing commits from now on.
