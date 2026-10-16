# AGENTS.md - ai-memory-context

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — switches, store layout, the four-stage export pipeline, the five
reconcile outcomes and the guardrails live there.

## Non-Negotiables

- **`scripts/redact.py`, `atomicity.py` and `session_marker.py` are byte-identical copies of
  `_mimisbrunnr-shared/scripts/`** — never imported. This skill must run where no `mimisbrunnr-*` is
  installed and ship as a single zip, and importing from a sibling folder is exactly the dependency
  that makes a zip broken alone. `tests/parity_shared_scripts.py` holds them equal. **Edit both sides
  in one change, never one.**
- **The store is local-first and there is no server, no network and no credential.** If a change needs
  one, the change is wrong for this skill — that is `mimisbrunnr-odin-context-memory`'s job, and
  Odin's output is the input here.
- **No `--publish`.** See LADR-001. Do not add one to "complete the set"; a half-ported publish that
  archived an unverified shape is worse than none.
- **The stamp keeps its `-HHmm`.** It is the sort key deciding which copy of a slug is current, and
  `updated` is required but **date-only**. A date-only stamp collapses a day's exports into one recency
  bucket and silently marks the wrong version current.

## Architecture Decisions

### LADR-001 — The store is local-first; durability is the user's filesystem, not a transport

- **Status:** Accepted · **Date:** 2026-10-16
- **Decision:** Knowledge lands in the gitignored `.context/context-memories/` and nowhere else. There
  is no `--publish` and no zip: durability is the user's own filesystem or VCS, and the skill states
  that rather than implying a backup it does not provide.
- **Consequence:** rejected — porting `ai-understanding`'s `--publish`/`--consume`. The store is a
  different shape in a different lifecycle, and a half-ported publish would archive an unverified
  layout. If this skill ever needs a transport, that is a new LADR, not a re-add.

### LADR-002 — The store layout is the run-folder shape, and the legacy shape is never written

- **Status:** Accepted · **Date:** 2026-10-16
- **Decision:** `<yyyyMMdd-HHmm>-<session-id>/<category>/<slug>.memory.md`. One export run makes one
  folder; the category is dateless and recurs across runs. `ai-understanding`'s legacy
  `<subject>-<stamp>/<slug>` shape is read and never written, so a store copied from either skill
  works unchanged.
- **Consequence:** the walk dispatches on **observed shape** — a top-level folder with no unit files
  directly inside it is read as a run folder of categories. Dispatching by name alone leaves the walk
  unable to see the nested layout at all, since every folder in a legacy-shaped store is a legacy run
  folder.

### LADR-003 — The pipeline is Odin's, minus the store-bound stages

- **Status:** Accepted · **Date:** 2026-10-16
- **Decision:** Redact → Dedupe → Atomicity → Write, in that order. Redact and atomicity are Odin's
  unchanged and still gate the rest. Odin's preflight folds into dedupe (the local `INDEX.md` scan
  replaces the cross-group query), the link derivation is dropped (no graph), and the transactional
  `set` becomes a file write.
- **Consequence:** `--approve` is dropped with no replacement — there are no proposed/approved store
  states, so the human veto moves to the proposal step **before** any write. That is a real loss of a
  durable queue, accepted deliberately: a veto that only holds inside one conversation is weaker, but a
  veto that can be skipped by a failed service is worse. `--deepsearch` is dropped with nothing.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-16 | Initial version. Local-first store, run-folder layout (LADR-002), Odin's pipeline with the store-bound stages removed (LADR-003), no `--publish` (LADR-001), three WET-vendored scripts held equal by `tests/parity_shared_scripts.py`. | — |