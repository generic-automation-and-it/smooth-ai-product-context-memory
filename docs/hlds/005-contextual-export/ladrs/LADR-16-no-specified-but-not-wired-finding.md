# LADR-16: No "specified but not wired" finding — a slice cannot evidence wiring

**Status:** Accepted — rejected option, 2026-10-01

## Context

"Proposed, not implemented" recurs in this repository's records: an unbuilt CLI-snapshot removal, HLD
status markers lagging shipped code. ICM Architect names the state `ghost` — named or filed, never wired —
beside `live` and `leftover`. The dossier's bounded taxonomy (NFR-04) has no category for it, so the
question is whether one can be added.

A finding must carry a basis and a scope (LADR-13), and an evidence-only category emits nothing without
evidence in the examined material (LADR-10's `near-miss-tag`). So the test is not whether the state is
worth reporting — it is — but whether a **slice of the store** can establish it.

The candidate basis is: a `decision` or `nfr` memory with no `implements` edge in the slice.

## Decision

**Rejected.** No `ghost`-style category is added to the taxonomy. The basis fails on two independent
grounds, either of which is sufficient:

1. **The slice cannot see the edge.** The bundle loads only edges whose source **and** target are both
   selected. A decision whose implementing memory sits outside the selection shows no `implements` edge,
   so absence in the slice is not absence in the store. The narrower the slice, the more false positives —
   the finding would be most confident exactly where it is least informed. This is the same reason the
   taxonomy says `no-links-in-slice` and never `orphan`.
2. **The store does not hold wiring.** Code is never a memory. `implements` is an optional link proposed
   by judgement at capture time, so its absence means "nobody captured and linked an implementing memory",
   not "the code does not do it". A category keyed on that absence is a table keyed on a value nothing
   reliably emits — and its false-positive rate is the store's capture coverage, which is unknown.

The half of the state the store **can** evidence is already reported: a `proposed` lifecycle status is
marked on the item (lifecycle marking), so "specified, not approved" is visible without a new category.

**Where the question belongs instead:** against the code. The repository bootstrap (`mimisbrunnr-ymir-
bootstrap`) already classifies each claim as *documented intention/proposal* versus *observed behavior —
supported by current code or tests*; a documented intention with no observed behavior behind it is the
`ghost` state, judged where the code is actually evidence.

## Consequences

- The taxonomy is unchanged, so every earlier dossier reads exactly as before. Its `none detected` was
  never a claim about wiring, and still is not.
- A caller-supplied `gap` may still name a specific unwired decision when one of `BR-27`'s three grounds
  supports it — the task, an included claim, or an explicit expectation (LADR-13). That is a judgement
  finding with a stated basis, not a mechanical category.
- **Reopen trigger:** the store gains a representation of code — a code-anchored vertex, or a writer that
  derives `implements` edges from the repository — so that absence of the edge could carry meaning, *and*
  the bundle loads boundary-crossing edges so the slice can see it.
