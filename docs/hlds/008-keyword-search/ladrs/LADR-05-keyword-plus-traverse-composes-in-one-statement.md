# LADR-05: Keyword anchors + bounded traverse compose in one round trip

**Status:** Draft

## Context

`deepsearch.py` fans out client-side: baseline 200, four keyword passes of 25, five
depth-one traversals of 20, 400-cap dedupe. Traversal therefore starts from a
client-truncated anchor list, costs up to ten round trips, and re-implements dedupe and
disclosure per caller. HLD-003 already composes relational reads with bounded AGE
traversal (depth 1–5) in one statement with the scope plan pushed in.

## Decision

**Offer** a server-side composition flag: one HTTP round trip in which the ranked
keyword anchor set (Goals 1–3, with the caller's `LIMIT`) feeds the HLD-003 bounded
widening. Execution order is fixed: `WHERE (FTS + scope + group selector)` →
`ORDER BY (rank iff operator query, else recency)` → `LIMIT` anchors →
widen survivors only → scope predicate on endpoints → drop out-of-selector endpoints
before any endpoint cap and count each distinct endpoint once
(`endpointsOutsideSelector`). A top-ranked out-of-scope anchor never consumes a widen
slot (negative control). Without the flag, existing fields are unchanged plus nullable
`Snippet`.

`depth` is required explicitly (`1..5`) when the flag is set — there is no default depth
(HLD-003 rejected one). Endpoints inherit anchor rank order. The composed call runs under
`SET LOCAL statement_timeout` with whole-records-only timeout semantics: on timeout the
handler keeps completed anchors, discloses the incomplete traverse, and never returns
partial endpoint rows.

## Disclosure

The response carries the `deepsearch` contract field-for-field: `anchorsEligible` and
`anchorsOmittedByCap` are `null` (not `0`) when the anchor phase never answers;
`stoppedEarly` (any early stop) vs. `budgetExhausted` (wall clock spent);
`passesIncomplete: [{kind, value}]` with one entry per incomplete phase (keyword-anchor,
traverse); `deadlineSeconds`; `endpointsOutsideSelector` as a distinct-endpoint count.
`anchorsForbidden` (403 on widen), `passesMalformed`, and the remaining deepsearch
disclosure fields are explicitly N/A for this shape and documented as such — not silently
absent.

## Alternatives Considered

- **Widen client-side only (status quo)** — rejected as the default for new callers:
  preserves ten round trips and truncation-before-traverse; kept working, not kept primary.
- **Unbounded or depth-6+ traverse** — rejected: exceeds the HLD-003 measured ceiling
  (depth 1–5 required); reopens the traversal NFR without evidence.

## Consequences

- One round trip replaces up to ten for the composed path; the recall deadline still bounds
  the whole call (HLD-004 LADR-01 pattern).
- Anchor `LIMIT` now controls traverse input — callers asking for 5 anchors get 5-anchor
  widening, which must be documented, not discovered.
- Dossier selection (HLD-005) can consume the same statement shape later without a new
  traversal semantic.

## Open

- None. The former default-depth question is closed: no default depth (HLD-003 rule).
