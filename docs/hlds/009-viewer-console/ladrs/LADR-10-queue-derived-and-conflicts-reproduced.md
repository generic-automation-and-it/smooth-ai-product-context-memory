# LADR-10 — The review queue is derived from the store; conflict choices are reproduced

## Context

The capture path's review queue is client-side and transient. If the Console tried to read it as a
persisted resource, it would need a new Host capability and would couple the UI to the skill's working
state.

## Decision

A proposal is a memory with `Status = proposed` (`SetMemories` validates `Proposed` / `Approved`), and
`QueryMemories` filters on `Status` + `IncludeProposed`. The Review view's queue is therefore a **query for
proposed memories** — derived from the store, no new Host resource. The **conflict-choice menus** and the
`set --dryrun` digest are capture-time judgements; the bridge **re-runs the capture pipeline's dedup** on a
selected proposal to reproduce them, rather than reading them back. They are not persisted.

## Consequences

- No new Host resource for the queue; it is queryable via existing fields.
- Conflict choices stay single-source (capture-time judgement, reproduced).
- The approve transition (proposed → approved) still needs a store capability — either an explicit
  transition or a `set` version-bump with `Status = approved`. Whether one exists is a P4 dependency to
  confirm, not an assumption.
- G1 decided 2026-10-15 for the automated export: kvasir `export` persists atomicity/decision-held
  candidates as `Status = proposed` (own preflight + veto, creates only; duplicates stay queued),
  so the store-derived queue has rows to read. The queue-file append stays as the local trail.

## Status

Draft
