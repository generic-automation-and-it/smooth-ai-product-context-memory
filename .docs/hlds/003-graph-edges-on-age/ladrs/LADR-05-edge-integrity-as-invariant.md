# LADR-05: Edge integrity becomes an enforced application invariant

**Status:** Draft

## Context

The relationship table provides two guarantees for free, and both are structural rather than
conventional:

- **Cascade deletion.** Foreign keys to the memory table mean deleting a memory removes every
  relationship touching it. No orphan can exist.
- **Edge uniqueness.** A composite primary key over source, target and relation means the same
  relationship cannot be recorded twice, while the same pair may legitimately hold several different
  relations.

A graph edge has neither. It carries no foreign key into a relational table, and the extension offers
no unique constraint over edges. Moving relationships to the graph therefore forfeits both.

This matters more here than it would elsewhere. The design has been explicit that constraints are
hard wherever they can be, precisely because two — subject uniqueness and ticket-to-group
uniqueness — are already soft and depend on the skill behaving correctly. Silently converting two
more into soft constraints would erode the property that makes the rest trustworthy.

## Decision

**Accept** the loss of both database-enforced guarantees and **replace** them with explicit
application invariants, each backed by a test that fails when the invariant is violated.

Deletion of a memory becomes a two-part operation within one transaction: remove the memory's edges,
then the memory. Because both halves run in the same transaction against the same instance, a failure
rolls back entirely and no partial state persists. This is the same discipline the version bump
already follows, and it is documented in the same place for the same reason.

Uniqueness becomes a read-before-write on the relationship path: an edge is created only when no edge
with that source, target and relation already exists. This mirrors how subject uniqueness is already
handled, and lands in the write path that already performs a cross-group lookup, so it costs no
additional traversal.

The honest framing is that this moves two guarantees from the strongest enforcement tier to the
second-strongest. That is a real cost, recorded here rather than discovered in production.

## Alternatives Considered

- **Tolerate orphan edges and filter them at read time** — rejected: makes every traversal pay for cleanliness forever, and an orphan edge is indistinguishable from a valid one pointing at a deleted memory.
- **A scheduled sweep that removes orphans** — rejected as the primary mechanism: introduces a window in which traversals return edges to memories that no longer exist. Retained as a possible audit, not a guarantee.
- **Keep the relationship table purely for its constraints and mirror into the graph** — rejected under LADR-03: dual-write with no arbiter.
- **Encode uniqueness in a deterministic edge property and enforce with a property index** — rejected for now: the extension indexes properties but does not enforce uniqueness through them, so this would document an intent without enforcing it.

## Consequences

- Deleting a memory can no longer be expressed as a single relational statement; the delete path gains an explicit graph step that must not be forgotten.
- Two guarantees move from structural to behavioural, raising the cost of a write-path bug. NFR-01 exists to bound that risk with verification.
- The invariants are enforced in one place — the relationship write and memory delete paths — rather than scattered, keeping the surface small.
- A direct database write that bypasses the application can now create an orphan or a duplicate, which the table made impossible. Operational access must treat relationships as application-owned.

## Open

- Whether an audit query that counts orphans is worth exposing as an operational check — decided during prototyping once the delete path has a test.

## Related

- **LADR-03** — the replacement that forfeits these guarantees.
- **NFR-01** — the verification that bounds the risk accepted here.
