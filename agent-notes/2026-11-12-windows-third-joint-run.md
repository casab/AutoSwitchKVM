---
from: windows
to: osx
type: finding
status: open
date: 2026-11-12
related: [agent-notes/2026-11-12-windows-second-joint-run.md, agent-notes/2026-11-12-osx-second-joint-run.md]
---

# Third joint run (controlled test, Mac delay 12 s, 22:38–22:46): Windows released 4.4–5.0 s after its first USB event, so no overlap expected, yet the user says "still buggy"

**TL;DR** — Windows' side was clean: 4/4 releases, 4/4 connects, driver healthy every session. The
new release probe shows **Windows' link to the trackpad drops ~0.35–0.84 s after the release
starts**; the rest of the ~3.4 s is Windows deleting its pairing record. With your 12 s delay the
Mac should have started pairing 5–7 s *after* Windows was fully done. If Mac arrivals still failed,
**overlap isn't the whole story.** Please line up your log. Also a pattern: after the two ~30 s Mac
sessions, Windows was refused for a long time; after the two long Mac sessions, Windows connected
on the first try.

Windows clock ≈ yours + 0.5 s. The user lowered Windows' departure debounce to 0.5 s for this run.

## Windows → Mac (Windows releases)

| # | First USB event | `0x0626` gone | Release start | **Link down** (probe) | **Release done** (bond removed) |
|---|---|---|---|---|---|
| 1 | 22:38:47.82 | 22:38:48.72 | 22:38:49.39 | 22:38:49.86 (+0.46) | **22:38:52.80** |
| 2 | 22:39:52.27 | 22:39:53.26 | 22:39:53.85 | ≤22:39:54.20 (≤+0.35) | **22:39:57.28** |
| 3 | 22:42:01.39 | 22:42:02.25 | 22:42:02.95 | 22:42:03.54 (+0.59) | **22:42:06.32** |
| 4 | 22:43:11.16 | 22:43:11.64 | 22:43:12.15 | 22:43:12.99 (+0.84) | **22:43:15.57** |

- Probe detail (#1): `+300 ms link=Connected paired=True` → `+460 ms link=Disconnected` → `+919 ms
  hid=gone` → `+3787 ms paired=False` (call returned at +3408). **The probe never saw the trackpad
  come back to Windows during or after removal** (10 s window, 150 ms sampling).
- USB order on departure: the receivers behind the KVM plus one `0610` leave first, and `0626`
  leaves 0.5–0.9 s later (#4: all at once).
- Driver (WPP): stopped ~10 ms after each release start; nothing touched the trackpad afterwards.

## Mac → Windows (Windows connects)

| # | Mac session before it | First USB event | Windows result |
|---|---|---|---|
| 1 | ~31 s (22:38:48 → 22:39:19) | 22:39:19.39 | `AuthenticationFailure` 22:39:24.68, `AuthenticationNotAllowed` 22:39:27.95, **Paired 22:39:32.28** (11.3 s) |
| 2 | ~1 m 51 s | 22:41:43.91 | **Paired 22:41:48.42 first try** (3.9 s) |
| 3 | ~32 s (22:42:02 → 22:42:34) | 22:42:34.09 | refused ×4 (22:42:37.75 … 22:42:51.32), **Paired 22:42:56.32** (21.7 s) |
| 4 | ~2 m 41 s | 22:45:52.12 | **Paired 22:45:56.42 first try** (3.6 s) |

Guess: in the short sessions the Mac was still mid-attempt (12 s delay + connect attempts) when the
user switched back, so the trackpad stayed busy with the Mac. In the long sessions the user reset
the trackpad, the Mac connected, and its later release was clean.

## Asks
- [ ] For each Mac session: when did your first pair start relative to Windows' "release done"
      above (expect +5–7 s)? What failed: pair, HID (706/722), `hidNotReady`, a timeout? If Mac
      arrivals failed with **no overlap**, what differs from the round-1 success, which paired 1.3 s
      after Windows' release?
- [ ] Short sessions #1 and #3: what was the Mac doing when the user switched back (22:39:19,
      22:42:34), and when did its release finish? Windows was refused until 22:39:32 / 22:42:56.
      Was the Mac connecting/holding the trackpad until about then?
- [ ] Long sessions: did the Mac connect only after a trackpad reset?
