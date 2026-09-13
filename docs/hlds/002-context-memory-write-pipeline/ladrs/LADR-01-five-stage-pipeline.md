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

1. **Preflight** — one *batched* cross-group read-before-write serving deduplication recall, link derivation and ticket uniqueness. Array in, array out. Writes nothing, judges nothing.
2. **Redact** — scrub detected secrets before anything reaches storage.
3. **Dedupe and derive links** — the semantic subject decision and typed-link derivation, both from the preflight's single traversal.
4. **Atomicity check** — confirm one memory is one fact; split bundles, route the remainder to skipped.
5. **Write** — one transactional call owned by the API.

The **decisions and the stages are two different lists** and do not map one-to-one. Deduplication
spans stages 1 and 3; link derivation likewise; ticket uniqueness lives entirely in stage 1. Conflating
the lists is the easiest way to misread the design, so the stage numbering is declared canonical and
any document numbering them differently is stale rather than an alternative reading.

The preflight is **batched** because a per-record pass misses intra-batch collisions — two candidates
in one batch sharing a subject, neither yet written, so neither is visible to the other.

## Alternatives Considered

- **Specify each concern independently** — rejected: ordering would emerge by accident, and three of the five are only correct in one position.
- **Per-record preflight** — rejected: misses intra-batch collisions entirely.
- **Three separate lookups for dedup, links and ticket uniqueness** — rejected: all three need the same cross-group subject traversal, so one read serves all three at no extra cost.
- **Let the skill sequence the write** — rejected: the version flip and insert must share one transaction, which only the API can guarantee.

## Consequences

- Ordering is a stated property, so a re-sequencing is visibly a change rather than a refactor.
- One traversal serves three concerns; the expensive part of the pipeline runs once.
- The pipeline is a unit — a change to one stage's position must justify itself against the others.
- Stage numbering must be maintained consistently across the contract documents, or the canonical claim becomes false.

## Related

- **LADR-02** — why redaction occupies stage 2 specifically.
- **LADR-05** — why link derivation shares stage 1's traversal.
