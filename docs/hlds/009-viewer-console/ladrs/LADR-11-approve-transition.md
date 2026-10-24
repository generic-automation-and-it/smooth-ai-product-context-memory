# LADR-11 — Approve is a gated `set` version-bump with `Status = approved`

## Context

G2: `SetMemories` already accepts `Status ∈ {proposed, approved}` per item, but nothing in the wire
shape distinguishes a fresh `approved` create from an `approved` transition. A direct write that
slipped an `approved` Status onto a create would skip the proposed stage and reach the store without
the review queue ever seeing it. The Console approve action needs a single, unambiguous transition
shape; a parallel "transition endpoint" would create a second write path that the capture gate does
not cover.

## Decision

The Console's approve is a `POST /api/context/memories` version-bump on the existing `Uuid` of the
proposed memory, with `Status = approved` on the new version. There is **no separate transition
endpoint**. The bridge re-builds the `MemoryWrite` from the stored proposed (and any post-amend
content), drives it through the capture pipeline's `set` invocation, and the store produces a new
`MemoryVersion` row at `NextVersion` via the existing `VersionExistingAsync` path. v1 (proposed)
becomes superseded; v2 (approved) is the new current. Append-only preserved.

**No validator special-case.** `Status` is validated only as `proposed` or `approved`, exactly as
before. A fresh approved memory is the capture path's *normal create* shape — `set_items` defaults
`status=approved` on a create (Uuid null, CreateUuid set), and the store's memory lifecycle is
approved-by-default, with `proposed` reserved for gate-held candidates awaiting review. Forbidding an
approved create would break the capture path and every integration test that seeds approved memories.
The approve transition (proposed → approved) is therefore just a version-bump with `Status=approved`
on the existing Uuid, which the standard `set` path already accepts. **LADR-11 adds no Host guard; it
records the transition model and the bridge plumbing only.**

**Gates re-run on the current body.** The bridge re-builds the `MemoryWrite` from whatever is
current in the review surface, including any post-amend edits. The approve is not a state transition
on the original capture-path body; it is a full re-submission evaluated by the capture gates
(redaction → atomicity → dedup → `set --dryrun` → write). An amend that would have been refused by
the capture path remains refused on approve.

**Bridge plumbing (P4b).** The bridge subprocesses the capture client per write so the
`cmd_set` stdout print does not contaminate the bridge's HTTP body (the same isolation pattern
`_fetch_blob` already uses for read paths; the write half is a parallel concern, owned by P4b).
The subprocess helper is implemented in P4b, not G2.

## Consequences

- One write path: `set` with `Status=approved` on a `Uuid`. No new Host endpoint.
- v1 (proposed) is visible in version history until the row is GC'd by an out-of-band retention
  decision; the queue query (P4a) already reads via `IncludeProposed` so the superseded proposed
  remains addressable for audit.
- The amend surface (Q3) is a version-bump with `Status=proposed` on the same `Uuid`, producing
  v2 (amended proposed). Approve then re-runs the gates and produces v3 (approved). The chain is
  visible in version history. **Out of G2 code scope**; the amend route is a P4b deliverable.
  Decision recorded here so P4b does not have to re-litigate the model.
- A same-claim re-approve of an approved memory is a no-op (the existing `MemoryClaim.Same` path
  short-circuits to `ItemMode.Unchanged`); a different-claim re-approve is a new approved version.
  Neither conflicts.
- The bridge subprocess pattern for the write path is the same shape as `_fetch_blob` for read
  paths; the helper belongs with the routes that consume it (P4b).

## Status

Implemented 2026-10-15 (G2 closed).

- SetMemories: unchanged `Status` rule (`proposed`/`approved`), no extra validator or handler guard.
- Tests: `SetMemoriesValidatorTests.Accepts_approved_create_without_uuid`,
  `Accepts_approved_version_bump_with_uuid`;
  `SetMemoriesHandlerTests.Approved_transition_creates_v2_with_approved_status`,
  `Approved_revision_creates_new_approved_version`.
- Bridge subprocess helper + write routes **delivered** in P4b (`bridge_p4b.py`; see the HLD AGENTS.md
  P4b row).
