# LADR-05: Link derivation batched into the pre-write round

**Status:** Accepted

## Context

Relationships between memories are modelled, indexed, and carry a mandatory reason. **Nothing creates
them.** No stage of any earlier specification owned their derivation.

Three simulation trials produced **zero links**, and in none of them did anyone notice. That is the
evidence: a capability with no owning stage is not merely unimplemented, it is invisible.

Derivation needs the same cross-group subject lookup that deduplication already performs.

## Decision

**Place** link derivation in the pre-write round, **batched with deduplication**, sharing its single
traversal.

Both questions — *is this the same subject as something we hold* and *what does this relate to* — are
answered from the same recalled candidate set. One read serves both.

Derived links are **reported in the digest**, never created silently, and each carries a reason. The
schema makes the reason non-nullable, so the justification is structurally forced rather than
encouraged.

Links are written **in the same transaction as the memories they relate**. A duplicate link inside a
write is skipped and counted, never fatal — a stale derived link must not discard the capture it
arrived with.

## Alternatives Considered

- **A separate background pass** — rejected: defers the value and needs its own trigger, which is another thing to forget.
- **Derivation at capture time** — rejected: too early, since the related memory may not exist yet.
- **A second, separately-confirmed call for links** — rejected: gives a true pre-write veto for links but costs a round-trip and leaves a window in which a memory exists without the edges that justify it.
- **Leave derivation to the human** — rejected: this is what produced zero links across three trials.

## Consequences

- The relationship model gains a creator, so it stops being decorative.
- Derivation costs nothing extra in reads, sharing deduplication's traversal.
- Links are created without individual confirmation; pre-write inspection is the dry-run over the whole batch (LADR-07).
- A wrong link is additive and cheap to remove — unlike a wrong version bump, which rewrites canon.

## Related

- **LADR-01** — the shared traversal.
- **LADR-07** — where derived links surface for inspection.
