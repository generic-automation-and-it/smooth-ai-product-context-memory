# LADR-04: Versioning absorbs supersession

**Status:** Accepted

## Context

The original design handled a changed claim by archiving the old record, creating a new one, and
linking them. It works, but it is versioning implemented a second time: it mints a new identity on
every change, and answering "what is current" requires a status filter, a null-check on a supersession
pointer, and link-walking for history.

It has a worse property. Carrying the surviving rationale forward from the archived record into its
replacement is a **manual discipline**, and manual disciplines are skipped under pressure — which is
exactly when a superseded decision most needs its reasoning preserved.

## Decision

**Replace** supersession-by-archive with versioning as the primary mechanism for a changed claim on an
unchanged subject.

Implementation is slowly-changing-dimension type 2: insert-only, with a current flag and a partial
unique index guaranteeing exactly one current version per memory. Nothing is deleted or overwritten.
The store retains all versions; **current-only is a retrieval default, never a storage rule.**

A supersession link survives for the rare case where a *different* subject renders a memory obsolete —
a relationship, not a version.

Two operational rules follow and are pinned where the code can find them. The bump must flip the old
current off **before** inserting the new one, because the partial unique index permits one current and
the insert would otherwise collide. Both statements run **in one transaction**, because they are
separate round-trips and a failure between them leaves the memory with *zero* current versions — a
state no constraint forbids and nothing detects.

## Alternatives Considered

- **Supersession-by-archive (the original)** — rejected as versioning built twice, with manual carry-forward.
- **Update in place, history in an audit table** — rejected: two write paths for one concept, and the audit table is exactly the versioned child row with a worse name.
- **Insert-only with no current flag, deriving current from max version** — rejected: cannot be expressed as a unique constraint, so "exactly one current" stops being enforceable.

## Consequences

- "What is current" is one predicate; history is the same table ordered by version.
- Carrying forward superseded reasoning is structural rather than remembered.
- Automatic version bumps are safe, because the worst case is a reversible misclassification rather than lost knowledge.
- Tables grow monotonically — accepted, and the current flag keeps retrieval cost flat.
- Retrieval must never return whole chains unasked, or a single query floods the caller.

## Related

- **LADR-03** — the split this operates over.
- **LADR-07** — the trigger that makes append-only structural.
