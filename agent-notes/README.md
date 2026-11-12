# Agent notes — cross-platform handoff between agents

Two Claude agents work on this repo in parallel: one on **Windows** (`windows/`) and one on
**macOS** (`osx/`), usually on different machines. This folder is how they tell each other about
findings, contract changes, and requests. It travels through **git**, so a note reaches the other
agent only after it is committed, pushed, and pulled.

## When to write a note

Write one when something you learned or changed matters to the other platform:

- **Contract changes:** `SPECIFICATION.md`, config JSON keys, `testdata/config-golden.json`,
  engine behavior that both engines must mirror.
- **Hardware findings:** device quirks, timings (e.g. "the trackpad refuses pairing for ~20 s
  after a switch"), failure modes seen in real logs. The other host's behavior is often only
  visible from your side.
- **Probably-shared bugs:** a bug you fixed whose pattern exists in the other codebase too.
- **Requests / questions:** something you need the other side to measure, verify, or port.
- **Decisions** that constrain both platforms.

Don't write notes for platform-only details; those go in that platform's `CLAUDE.md`.

## Format

One note per topic, one file per note:
`agent-notes/YYYY-MM-DD-<from>-<short-slug>.md` (e.g. `2026-11-12-windows-failure-classification.md`).
Unique filenames mean two agents never edit the same new file, so notes don't conflict.

```markdown
---
from: windows            # windows | osx
to: osx                  # osx | windows | all
type: heads-up           # finding | heads-up | request | question | decision
status: open             # open | acknowledged | done | wontfix
date: 2026-11-12
related: [SPECIFICATION.md §3, windows/src/AutoSwitchKVM.Core/SelectionEngine.cs]
---

# One-line title

**TL;DR** — two or three sentences.

## Details / evidence
Short log excerpts with timestamps, file:line references, what was tried.

## Asks
- [ ] Concrete thing for the recipient to do / check / answer.
```

## Replying and closing

- **Never rewrite the other agent's text.** Reply by appending a section at the end:
  `## Reply — osx (2026-11-13)` (or `windows`), then update `status:` in the front matter.
- Recipient sets `acknowledged` once read and `done` / `wontfix` once acted on (tick the asks you
  handled). Either side may add more replies; keep each one short.
- Once a finding is settled, fold the durable part into `SPECIFICATION.md` or the relevant
  `CLAUDE.md` and mark the note `done`. Notes are a queue, not the knowledge base.

## Session routine

1. **At session start:** `git pull --ff-only`, then list open notes for you. Use `osx` or
   `windows` for your side (works in bash, zsh, and Git Bash):

   ```sh
   grep -lE '^status: (open|acknowledged)' agent-notes/2*.md | xargs grep -lE '^to: (osx|all)'
   ```

   Read each one before starting related work, and reply / update its status.
2. **When you write or reply to a note:** commit it with the related change. Agents commit and
   push only when the user asks, so if you haven't been asked, **tell the user a note is waiting
   to be committed/pushed** so it reaches the other agent.
3. **Pull before writing** a reply, to avoid conflicts on the shared note file.

Optional live ping: if the other session shows up in `ListAgents` (e.g. through Remote Control),
`SendMessage` can say "new note: agent-notes/<file>". The note stays the record either way.
