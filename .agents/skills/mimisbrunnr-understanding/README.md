## Switches

The three operations are **never conflated** — a load is context-injection, an import is an opt-in
write, a dump is an export. All switches are **off by default**.

| Operation | Default | Writes to store? |
|---|---|---|
| `load` | context-injection | no |
| `import --store` | opt-in | yes, through the capture path |
| `dump --currentsession` | export to local folder | no |

# mimisbrunnr-understanding

Load a Mímisbrunnr **Understanding** export — or any prior material — into a new or running agent's
session context, and share context across sessions and repositories.

## Usage

```bash
# Load a store export or a foreign document into context (no write)
# <input> may be a store export, an ai-understanding .understanding.md unit or store folder, or a dump folder
python3 .../understanding_client.py load <input> \
  [--format store|understanding|foreign|auto] [--all] [--asof YYYY-MM-DD] [--max-chars N]

# Capture material back into the store (opt-in --store), bound by selectors
python3 .../understanding_client.py import <input> --store \
  [--tickets A,1] [--tags tag] [--repository repo] [--scope product:x]

# Dump the current session's context to a discoverable local folder (export, no write)
python3 .../understanding_client.py dump --currentsession [--out .context/mimisbrunnr-understandings/<folder>]
```

## Design

- **Load is non-destructive.** The default load injects material as cited grounding context and writes
  nothing (HLD-007 NFR-01).
- **Import is opt-in and funnels through the capture path.** `--store` hands material to the existing
  capture path — atomicity, redaction, dedup/link — never a direct write (HLD-007 LADR-03).
- **The session dump is an export.** `--currentsession` writes to
  `.context/mimisbrunnr-understandings/<session-folder>/`, with a fitting folder name reported on output so another
  agent can discover it (HLD-007 LADR-07). It changes nothing in the store, and its content is redacted
  before it is written.
- **ai-understanding files are structured input.** A `.understanding.md` unit, or a whole store folder
  (newest version per slug), loads as question/answer/why/boundaries rather than raw prose (HLD-007 LADR-09).

Business authority: [BRD-003](../../../docs/brd/003-understanding-transfer/). Design:
[HLD-007](../../../docs/hlds/007-understanding-transfer/).

## Test

```bash
python3 -B .agents/skills/mimisbrunnr-understanding/tests/run_tests.py
```

## How this relates to ai-understanding and to harness compaction

This skill moves knowledge; it does not decide what qualifies. `ai-understanding` writes and curates the
`.understanding.md` units; this skill loads them into context, imports them into the store with
`--store`, or dumps the session for another agent.

```
session ──ai-understanding --export──▶ .context/understandings/<subject>-<stamp>/<slug>.understanding.md
                                          ├─ load <folder>        → context only, no write
                                          └─ import --store       → capture path → store (kind = understanding)
session ──dump --currentsession──▶ .context/mimisbrunnr-understandings/<name>/_session.md
```

Agent harnesses compact a conversation automatically when the context window fills, replacing the
transcript with a summary so the current task can continue. `dump --currentsession` is the closest thing
here — a regenerated, replaced-not-appended Markdown projection — but it lands on disk, redacted, where
another session or repository can `load` it.

| | Harness compaction | This skill |
|---|---|---|
| Trigger | Automatic, near the context limit | On request |
| Reach | One session, one agent | Other sessions, repositories, and the store |
| Loading | Whole summary always in context | `load` picks inputs; store exports default to understanding-only (`--all` widens) |
| Safety | No redaction; summary read as fact | Dump redacted before write; loaded material cited as data, never instructions |
| Durability | Gone with the session | Local dump or folder; `--store` for the durable store |

**Pros:** knowledge survives the session and crosses repositories; loading is selective and cited;
nothing is written unless `--store` is passed; the dump fails closed if redaction cannot run.

**Cons:** nothing happens automatically — someone must dump, load or import; the dump is a projection
that re-dumping replaces, so hand edits are lost; importing an `ai-understanding` unit is lossy at the
edges (prose boundaries ride in `contentSummary`, `scope` becomes an advisory `portability` key); and
the quality of what is loaded is only as good as what `ai-understanding` curated.

**Using them together:** keep compaction for in-session continuity; before a clear or handoff, export
with `ai-understanding` (knowledge) or `dump --currentsession` (task continuity); in the next session,
`load` the folder, and `import --store` only units that have earned a place in the store.
