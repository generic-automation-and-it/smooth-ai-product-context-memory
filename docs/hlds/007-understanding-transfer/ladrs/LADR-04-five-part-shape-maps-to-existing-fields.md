# LADR-04: The five-part Understanding shape maps onto existing memory fields; no new column

**Status:** Draft

## Context

BRD-003 BR-38 gives an Understanding five parts: **trigger** (the situation in which it applies),
**the knowledge** (direct operational guidance), **why** (the reasoning or failure that produced it,
enough to judge edge cases), **boundaries** (where it stops applying) and **provenance** (date and
what it was learned from).

The question is whether these need new stored fields. The user's instruction was to reuse existing
memory fields, but to **review whether any part should be incorporated** as a dedicated field.

A memory already carries: subject/name (slug), description, statement, contentSummary (AI TL;DR),
kind, confidence, status, `ValidFrom`/`ValidUntil` (business time), sources (provenance, list),
facets (tags) and a summary stamp. The whole point of LADR-01 is that an Understanding is a memory,
so the re-use instinct is right. The only part that does not map cleanly is **trigger** — a
recognition condition ("when this applies") that is more operational than a subject description.

## Decision

Map the five parts onto existing fields, and add **no new column**:

| Understanding part | Stored field |
|---|---|
| **The knowledge** | `Statement` |
| **Why** | `ContentSummary` (AI TL;DR of the reasoning/failure) |
| **Trigger** | `Description` — the situation in which the Understanding applies |
| **Boundaries** | `ValidUntil` (where it stops applying in time) + the subject's scope/applicability |
| **Provenance** | `Sources` (list, each with shape marker) + `ValidFrom` + `CreatedOn` |

**Trigger maps to `Description`.** A trigger is the recognition condition; the memory's description is
the natural home for "this applies in this situation". This is the one judgement call, and it avoids a
schema change. If use shows that `trigger` is genuinely distinct enough to need a dedicated field,
that is a follow-up — validate by use, matching HLD-005's stance on its unvalidated focuses — not a
design decision made ahead of evidence.

## Alternatives Considered

- **Add a dedicated nullable `Trigger` column** — rejected for now: it is a schema change (EXPORT_AGENTS
  forbids it), and `Description` already carries the situation. Flagged as the follow-up if use proves
  it insufficient. (The user's "review if any should be incorporated" observation is answered here: the
  one part that doesn't cleanly map is `trigger`, and it is mapped to `Description` rather than a new
  column, on the no-schema-change guardrail.)
- **Flatten trigger into `Statement`** — rejected: the knowledge must stay clean operational guidance;
  mixing the trigger in muddies it.
- **Store the five parts as a JSON document** — rejected: a JSON blob is not queryable by label or
  similarity and breaks the atomic-fact model.

## Consequences

- No migration is required for the Understanding shape (the `kind` constant is a value, not a schema
  change).
- An Understanding is rendered from the same fields as any memory, so the export path needs no new
  shape handling beyond recognising the kind.
- The `trigger`→`Description` mapping is the single judgement call; it is recorded here so it can be
  revisited if use demands a dedicated field.
