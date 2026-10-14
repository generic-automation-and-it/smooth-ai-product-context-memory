# LADR-05: Keyword anchors + bounded traverse compose in one round trip

**Status:** Draft

## Context

`deepsearch.py` fans out client-side: baseline 200, four keyword passes of 25, five
depth-one traversals of 20, 400-cap dedupe. Traversal therefore starts from a
client-truncated anchor list, costs up to ten round trips, and re-implements dedupe and
disclosure per caller. HLD-003 already composes relational reads with bounded AGE
traversal (depth 1–5) in one statement with the scope plan pushed in.

## Decision

**Offer** a server-side composition flag: one HTTP round trip (handler transaction at
READ COMMITTED;
anchor phase then widen phase, one snapshot iff a single composed statement proves out
at Prototype — otherwise two statements, one transaction, documented as such) in which
the ranked keyword anchor set (Goals 1–3, with the caller's `LIMIT`) feeds the HLD-003
bounded widening. Execution order is fixed: `WHERE (FTS + scope + group selector)` →
`ORDER BY (rank iff operator query, else recency)` → `LIMIT` anchors →
widen survivors only with the scope gate on every hop (HLD-003 `HiddenDimensions` rule —
endpoint-only gating is the closed regression, not repeated) → drop out-of-selector
endpoints before any endpoint cap and count each distinct endpoint once
(`endpointsOutsideSelector`). A top-ranked out-of-scope anchor never consumes a widen
slot (negative control). Without the flag, existing fields are unchanged plus nullable
`snippet`.

`depth` is required explicitly (`1..5`, missing/out-of-range refused with
`E_INVALID_DEPTH`) when the flag is set — there is no default depth (HLD-003 rejected
one). Endpoints inherit anchor rank order (multi-parent takes the best anchor rank;
ties break by endpoint uuid). The composed call runs under `SET LOCAL statement_timeout`
(Prototype-measured value; the odin skill's `RECALL_DEADLINE_SECONDS`, 60 s client-side
per its LADR-006, bounds the whole call and wins on conflict — timeout is
defence-in-depth, never primary) with whole-records-only
semantics: query-cancel maps to `stoppedEarly` (traverse incomplete, anchors kept) vs.
`budgetExhausted` (wall clock spent); non-timeout AGE errors after successful anchors
keep the anchors and disclose the traverse as incomplete; partially widened rows are
never returned, and no `ts_headline` is evaluated on dropped rows.

## Disclosure

The response carries the `deepsearch` disclosure subset: `anchorsEligible` and
`anchorsOmittedByCap` are `null` (not `0`) when the anchor phase never answers (and
`anchors` is then empty); `stoppedEarly` (any early stop) vs. `budgetExhausted` (wall
clock spent); `passesIncomplete: [{kind, value}]` where `value` is the phase name only
(keyword-anchor, traverse — never lexemes or uuids); `deadlineSeconds`;
`endpointsOutsideSelector` as a distinct-endpoint count (accepted existence oracle, bounded
by the endpoint cap — stated, not hidden).
`anchorsForbidden` (403 on widen), `passesMalformed`, and the remaining deepsearch
disclosure fields are explicitly N/A for this shape and documented as such — not silently
absent, and the shape is therefore a documented subset, not field-for-field equality.

## Alternatives Considered

- **Widen client-side only (status quo)** — rejected as the default for new callers:
  preserves ten round trips and truncation-before-traverse; kept working, not kept primary.
- **Unbounded or depth-6+ traverse** — rejected: exceeds the deliberate HLD-003 ceiling
  (depth 1–5 required, depth three targeted with two hops headroom); reopens the
  traversal NFR without evidence.

## Consequences

- One round trip replaces up to ten for the composed path; the odin skill's recall
  deadline (`RECALL_DEADLINE_SECONDS`, its LADR-006) bounds the whole call.
- Anchor `LIMIT` now controls traverse input — callers asking for 5 anchors get 5-anchor
  widening, which must be documented, not discovered. A traverse anchor cap below the
  page `LIMIT` is set at Prototype if the LIMIT×depth explosion breaches the timeout
  budget.
- Dossier selection (HLD-005) can consume the same statement shape later without a new
  traversal semantic.

## Open

- None. The former default-depth question is closed: no default depth (HLD-003 rule).
  Dossier reuse (HLD-005 consuming this shape) is a future consumer question, not a
  consequence asserted here: rank order ≠ dossier topo order, so fit is undecided.
