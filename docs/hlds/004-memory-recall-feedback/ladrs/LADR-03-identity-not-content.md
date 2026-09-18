# LADR-03: Feedback carries identity and outcome only

**Status:** Accepted — 2026-09-18

## Context

The most useful feedback record would include the query that was asked and something of what was
returned, because that is what makes a miss diagnosable months later.

It is also exactly what the store's confidentiality requirement forbids. Memory content must never
appear in a log, span attribute or metric label — and a query against this store is itself sensitive,
frequently containing the subject matter of commercially confidential reasoning.

A feedback record is a new surface with the same exposure and none of the existing scrutiny.

## Decision

**Restrict** feedback to identity, outcome and time. No subject, no claim, no summary, no query text.

A record answers *which memory was returned, to what kind of retrieval, when* — and for a miss, *that
a retrieval of some shape returned nothing*. Whatever shape-classification is recorded must be a
bounded category rather than free text, because free text is where content leaks back in.

The cost is honest: a miss recorded without its query is harder to diagnose. That cost is accepted.
The alternative creates a second, less-guarded copy of the sensitive material the primary store is
careful with, which would be a poor trade for diagnostic convenience.

## Alternatives Considered

- **Record the query text** — rejected: the query is often as sensitive as the content, and this would create an unguarded second copy.
- **Record a hash of the query** — rejected: a hash groups repeated identical queries and nothing else, since natural-language queries rarely repeat verbatim. Real cost, negligible benefit.
- **Record content for misses only** — rejected: misses are the case most likely to be examined later, so this concentrates exposure exactly where retention would be longest.

## Consequences

- Feedback cannot leak what the store protects, because it never holds it.
- A miss is countable and classifiable but not reconstructable; diagnosing a specific past miss requires the operator to remember it.
- Bounded categories must be defined up front, since adding one later changes the meaning of existing records.

## Open

- ~~The set of retrieval-shape categories. Must be small, bounded, and defined before the first record is
  written.~~ **Resolved — 2026-09-18: closed to five shapes, schema-constrained.** `free_text`,
  `facet_only`, `ticket_scoped`, `group_scoped`, `unfiltered` (`RetrievalShape` in
  `Application/Abstractions/IRecallFeedback.cs`). Enforced by the `ck_recall_feedback_shape` CHECK
  constraint on `recall_feedback`, so an out-of-set value is rejected by the schema (NFR-01), not by
  convention. Classification is produced by `RecallShapeClassifier` from the raw retrieval request.

## Related

- **NFR-01** — the verification of this decision.
