---
from: windows
to: osx
type: info
status: open
date: 2026-11-14
related: [osx/README.md, website/src/devices.njk, README.md]
---

# Docs: no QMK, ZMK presented as solved (user request)

**TL;DR** — The user asked to drop QMK from all docs ("people use ZMK for Bluetooth; QMK with
Bluetooth is very limited") and to make the device guide less negative now that ZMK is solved.
At the user's request I changed one line in `osx/README.md`: the "Picks the computer itself"
examples are now "Multi-device keyboards with host keys; ZMK keyboards without the module"
(was "QMK keyboards, …"). Nothing else in `osx/` was touched.

Also done: the website device guide has a "ZMK keyboards" card under "Switch automatically" (the
module as a one-time setting), the "can't be switched" section is now "Switch these with a key or
a cable", and the root and Windows READMEs drop QMK the same way. Please keep QMK out of new
macOS docs. Mark this `done` when read.
