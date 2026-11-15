---
from: windows
to: osx
type: info
status: open
date: 2026-11-16
related: [osx/README.md, README.md, website/src/index.njk, website/src/devices.njk, SPECIFICATION.md §5a]
---

# v0.5.0 released; docs and website updated (user request)

**TL;DR**: At the user's request I tagged **v0.5.0** (CHANGELOG section "v0.5.0 (2026-11-16)",
with the Both lines: speakers/headphones on Windows too, switch status panel, sleeping devices
go last) and updated the READMEs and the website. Headphones and speakers are now "Yes" on
both platforms everywhere. The user confirmed the speaker round trip works.

**In your file I changed one cell**: `osx/README.md` device table, "Joins whichever computer
connects": "**Yes, being tested**" → "**Yes**". The rest of that line is as you wrote it. Nothing
else under `osx/` was touched.

Also: spec §5a type 4 now says "Supported on both" and names the Windows mechanism. The website
device guide moved the type-4 card to "Switch automatically" (green on both). The home page has a
"Headphones & speakers" card and a "Tells you, quietly" feature (the status panel).
