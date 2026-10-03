## Switches

The four operations are **never conflated** — a load is context-injection, an import reads the store, an
export writes it, and a dump writes the session to a local folder. All switches are **off by default**.

| Operation | Direction | Reads store? | Writes store? |
|---|---|---|---|
| `load` | file/folder → session | no | no |
| `import` | store → session | yes (read token only) | no |
| `export` | session → store | yes | only with `--write` |
| `dump --currentsession` | session → local folder | no | no |

# mimisbrunnr-kvasir-understanding

Load a Mímisbrunnr **Understanding** export — or any prior material — into a new or running agent's
session context, and share context across sessions and repositories.

## Usage

```bash
# Load a store export or a foreign document into context (no write)
# <input> may be a store export, an ai-understanding .understanding.md unit or store folder, or a dump folder
python3 .../understanding_client.py load <input> \
  [--format store|understanding|foreign|auto] [--all] [--asof YYYY-MM-DD] [--max-chars N]

# Recall understanding-kind records from the live store into the session (read token only, no write)
python3 .../understanding_client.py import \
  [--ticket provider:key] [--repository repo] [--initiative NAME] [--scope scope:id] \
  [--tags tag,...] [--query WORD] [--status STATUS] [--limit N] [--all] [--table]

# Capture material into the store through the capture path (dry run unless --write)
python3 .../understanding_client.py export <input> [--write] \
  [--tickets A,1] [--tags tag] [--repository repo] [--scope product:x]

# Dump the current session's context to a discoverable local folder (export, no write)
python3 .../understanding_client.py dump --currentsession [--out .context/mimisbrunnr-understandings/<folder>]
```

## Design

- **Load is non-destructive.** The default load injects material as cited grounding context and writes
  nothing (HLD-007 NFR-01).
- **Import is read-only.** It queries the store through the capture skill's read client — read token only,
  no write capability — and renders the recalled records as cited grounding context (HLD-007 LADR-11).
- **Export funnels through the capture path.** `export <input>` hands material to the existing capture
  path — atomicity, redaction, dedup/link — never a direct write, and it dry-runs unless `--write` is
  passed (HLD-007 LADR-03). An over-20-candidate batch auto-splits into consecutive ≤20 chunks, each
  processed end to end, and a candidate whose subject already exists in the target group is sent as a
  version bump rather than a create that 409s.
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
python3 -B .agents/skills/mimisbrunnr-kvasir-understanding/tests/run_tests.py
```

## How this relates to ai-understanding and to harness compaction

This skill moves knowledge; it does not decide what qualifies. `ai-understanding` writes and curates the
`.understanding.md` units; this skill loads them into context, exports them into the store, or dumps the
session for another agent.

```
session ──ai-understanding --export──▶ .context/understandings/<subject>-<stamp>/<slug>.understanding.md
                                          ├─ load <folder>     → context only, no write
                                          └─ export <folder>   → capture path → store (kind = understanding)
store   ──import───────────────────▶ session (kind = understanding, read token only)
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
| Durability | Gone with the session | Local dump or folder; `export` for the durable store |

**Pros:** knowledge survives the session and crosses repositories; loading is selective and cited;
nothing is written unless `export --write` is passed; the dump fails closed if redaction cannot run.

**Cons:** nothing happens automatically — someone must dump, load, import or export; the dump is a
projection that re-dumping replaces, so hand edits are lost; loading an `ai-understanding` unit is lossy
at the edges (prose boundaries ride in `contentSummary`, `scope` becomes an advisory `portability` key);
and the quality of what is loaded is only as good as what `ai-understanding` curated.

**Using them together:** keep compaction for in-session continuity; before a clear or handoff, export
with `ai-understanding` (knowledge) or `dump --currentsession` (task continuity); in the next session,
`load` the folder, `import` what the store already holds, and `export --write` only units that have
earned a place in the store.
