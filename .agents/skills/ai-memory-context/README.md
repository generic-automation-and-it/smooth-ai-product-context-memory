# ai-memory-context

A **local-first** memory store for agent sessions. Knowledge lands in `.context/context-memories/` in
your own workspace — no server, no network, no credentials, no sibling script folder.

That constraint is the design. The capture pipeline is the valuable part, and a pipeline that needs a
service running is a pipeline most people never run.

## Store layout

```
.context/context-memories/
  INDEX.md                       # generated — one row per slug, newest version
  <yyyyMMdd-HHmm>-<session-id>/  # one export run
    <category>/
      <slug>.memory.md
  _unfiled/                      # exempt from the stamp
```

One export run is one folder. Categories are nested and dateless, so they recur across runs —
`**/<category>/*.md` answers "all X ever", and `ls` on the store shows what a session produced.

The legacy `<subject>-<yyyyMMdd-HHmm>/<slug>.understanding.md` shape is **read and never written**, so
a store copied from `ai-understanding` works unchanged.

## Use

```bash
# resolve the run folder ONCE per export, reuse the value verbatim
python3 .agents/skills/ai-memory-context/scripts/memory_index.py --run-folder

# after writing, regenerate the index and report every problem
python3 .agents/skills/ai-memory-context/scripts/memory_index.py

# advisory decay report
python3 .agents/skills/ai-memory-context/scripts/memory_index.py --review
```

## Durability

There is **no `--publish`**. The store is gitignored workspace state; durability is your filesystem or
your VCS, and the skill says so rather than implying a backup it does not provide. See
[LADR-001](AGENTS.md).

## Pipeline

Odin's capture pipeline with the store-bound stages removed: **redact → dedupe → atomicity → write**.
Redact and atomicity are unchanged and still gate the rest. `--approve` is dropped with no
replacement — there are no proposed/approved store states, so the human veto sits before the write.

## Shipping

The skill has **no dependency outside itself** — the three scripts it uses are byte-identical copies
of the shared originals, held equal by `tests/parity_shared_scripts.py`. So it ships as one zip:

```bash
python3 scripts/pack-skill-zip.py ai-memory-context
```

## Provenance

`mimisbrunnr-odin-context-memory`'s pipeline, reshaped. Nothing here talks to a store; feeding Odin's
output into this skill's store is the supported way to combine them.
