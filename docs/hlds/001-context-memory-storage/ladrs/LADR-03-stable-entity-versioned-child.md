# LADR-03: Stable entity row with a versioned child row

**Status:** Accepted

## Context

A memory has a **subject** — what it is about — and a **claim** — what it currently asserts. These have
different lifecycles: deduplication matches on the subject, while versioning replaces the claim.

An earlier two-field design failed precisely here. With only a claim and its evidence, the subject
existed nowhere addressable, so subject-matching had nothing to match on.

A second force settled the shape. Classification — tags and facets — is deliberately **unversioned**,
because a tag states findability rather than a claim about the world. Data attached to a versioned row
is versioned by construction, so unversioned classification requires a row that does not version.

## Decision

**Split** the memory into a stable logical row and a versioned child row.

The stable row carries identity, group membership, the subject and its normalised slug, and the
unversioned classification. The versioned child carries the claim, the summary, the content reference,
kind, confidence, status, both time axes and provenance.

Three identifiers exist per row: a narrow surrogate for joins, a **stable identity that survives
versions** and is the only identity exposed outside the store, and a **lineage identifier shared by
clones** of one fact recorded against different repositories. The third is forced by the clone rule —
clones need distinct identities, or two rows each claim to be the current version of one memory.

This mirrors the group and its versioned description, so the model is stable-parent and versioned-child
at both levels rather than two unrelated shapes.

## Alternatives Considered

- **A single flat memory table** — rejected once classification was confirmed unversioned; unversioned data cannot live on a versioned row.
- **Subject and claim in one field** — rejected: destroys "same subject, new claim", which is the hot path.
- **Repeating subject on every version** — rejected as the latent error the split removes: the subject is stable by definition, so repeating it invites divergence between versions of one memory.

## Consequences

- "Same subject, new claim" is directly expressible, and deduplication has a stable field to match on.
- Subject uniqueness within a group needs no current-version predicate, because there is exactly one stable row per memory.
- Reading a memory's current state requires a join, accepted given retrieval volume.
- Clones are retrievable as a set and drift between them is detectable.

## Related

- **LADR-04** — what happens when the claim changes.
- **LADR-07** — the guard that keeps classification off the versioned row.
