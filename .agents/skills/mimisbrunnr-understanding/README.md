# mimisbrunnr-understanding

Load a Mímisbrunnr **Understanding** export — or any prior material — into a new or running agent's
session context, and share context across sessions and repositories.

Three operations:

| Operation | Default | Writes to store? |
|---|---|---|
| `load` | context-injection | no |
| `import --store` | opt-in | yes, through the capture path |
| `dump --currentsession` | export to local folder | no |

## Usage

```bash
# Load a store export or a foreign document into context (no write)
python3 .../understanding_client.py load <input> [--format store|foreign] [--asof YYYY-MM-DD]

# Capture material back into the store (opt-in --store), bound by selectors
python3 .../understanding_client.py import <input> --store \
  [--tickets A,1] [--tags tag] [--repository repo] [--scope product:x]

# Dump the current session's context to a discoverable local folder (export, no write)
python3 .../understanding_client.py dump --currentsession [--out .context/understandings/<folder>]
```

## Design

- **Load is non-destructive.** The default load injects material as cited grounding context and writes
  nothing (HLD-007 NFR-01).
- **Import is opt-in and funnels through the capture path.** `--store` hands material to the existing
  capture path — atomicity, redaction, dedup/link — never a direct write (HLD-007 LADR-03).
- **The session dump is an export.** `--currentsession` writes to
  `.context/understandings/<session-folder>/`, with a fitting folder name reported on output so another
  agent can discover it (HLD-007 LADR-07). It changes nothing in the store.

Business authority: [BRD-003](../../../docs/brd/003-understanding-transfer/). Design:
[HLD-007](../../../docs/hlds/007-understanding-transfer/).

## Test

```bash
python3 -B .agents/skills/mimisbrunnr-understanding/tests/run_tests.py
```
