# LADR-01: Five stages in a fixed order, specified together

**Status:** Accepted

## Context

Five concerns govern a write: redaction, deduplication, link derivation, atomicity and ticket
uniqueness. Each looks independently specifiable. They are not — four are stages of one pipeline and
the fifth is a property of its first stage, and each one's correct position depends on the others.

Specify them separately and the ordering emerges by accident. Redaction placed after the body write is
useless; deduplication placed after the write cannot change the write decision; an atomicity check
placed after the write has nothing left to prevent.

## Decision

**Specify** all five together as one pipeline with a **fixed, canonical order**, executed in that order
and never re-sequenced.

1. **Preflight** — one *batched* exact cross-group read-before-write serving subject and ticket
   backstops plus intra-batch collision detection. Array in, array out. Writes nothing, judges nothing.
   Ticket uniqueness is the one concern that needs the caller's target group: "already owned by
   **another** group" is undecidable without it, so a candidate declares the group it is bound for and
   self-ownership is not a conflict. Subject recall stays group-blind.
2. **Redact** — scrub detected secrets before anything reaches storage.
3. **Dedupe and derive links** — semantic subject judgement and typed-link derivation use bounded
   `/query` recall in addition to preflight facts. Optional deep search adds bounded keyword and
   one-hop graph passes; it does not change authority or scope.
4. **Atomicity check** — confirm one memory is one fact; split bundles, route the remainder to skipped.
5. **Write** — one transactional call owned by the API.

The **decisions and the stages are two different lists** and do not map one-to-one. Exact deduplication
spans stages 1 and 3; semantic recall and link derivation live in stage 3; ticket uniqueness lives
entirely in stage 1. Conflating
the lists is the easiest way to misread the design, so the stage numbering is declared canonical and
any document numbering them differently is stale rather than an alternative reading.

The preflight is **batched** because a per-record pass misses intra-batch collisions — two candidates
in one batch sharing a subject, neither yet written, so neither is visible to the other.

## Alternatives Considered

- **Specify each concern independently** — rejected: ordering would emerge by accident, and three of the five are only correct in one position.
- **Per-record preflight** — rejected: misses intra-batch collisions entirely.
- **Unbounded or per-candidate semantic recall** — rejected: cost multiplies with batch size and raw
  rows flood the working session. Stage 3 recall is batched, bounded and delegated.
- **Let the skill sequence the write** — rejected: the version flip and insert must share one transaction, which only the API can guarantee.

## Consequences

- Ordering is a stated property, so a re-sequencing is visibly a change rather than a refactor.
- Exact checks are one batched preflight; semantic recall is a separate bounded read owned by stage 3.
- The pipeline is a unit — a change to one stage's position must justify itself against the others.
- Stage numbering must be maintained consistently across the contract documents, or the canonical claim becomes false.

## Related

- **LADR-02** — why redaction occupies stage 2 specifically.
- **LADR-05** — why link derivation shares stage 1's traversal.
