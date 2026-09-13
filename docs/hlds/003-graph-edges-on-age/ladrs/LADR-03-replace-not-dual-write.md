# LADR-03: Replace the relationship table outright; never dual-write

**Status:** Draft

## Context

Introducing a new store invites a transitional period in which both the old table and the new graph
are written, so the old path keeps working while the new one is proven. It reads as the cautious
option.

It is the opposite. Dual-write means two representations of the same relationship with no arbiter,
and every read must choose which to trust. The store has no deployed data and no external consumers:
the relationship table exists, but nothing outside the service depends on its shape.

## Decision

**Replace** the relational relationship table with graph edges in a single change. There is no period
during which both are authoritative.

The graph becomes the only place a memory relationship is recorded. The table is dropped in the same
change that creates the graph, and the relationship-bearing feature slices are re-pointed at the
graph in that change. Existing relationship rows, if any, are carried over as part of the same
migration.

Reversal is by migration, not by fallback. If the design proves wrong, the correcting change restores
the table — it does not switch a flag, because no flag exists.

## Alternatives Considered

- **Dual-write with the table authoritative** — rejected: two representations, no arbiter, and every read must pick a side.
- **Dual-write with the graph authoritative** — rejected: same defect, plus the table becomes a stale artefact that looks current.
- **Additive introduction, leaving the table for one-hop and using the graph for multi-hop** — rejected: splits one concept across two stores permanently and guarantees they disagree the first time a write path misses one.
- **Feature flag between implementations** — rejected: a flag over two stores is dual-write with extra steps.

## Consequences

- One representation of a relationship at all times; no reconciliation logic and no "which is right" question.
- The change is larger and lands at once, so it cannot be partially shipped.
- Rollback is a migration rather than a configuration change, which is slower but leaves no ambiguous intermediate state.
- The relational schema no longer fully describes relationships, so schema documentation must point explicitly at the graph.
- The entity-count guard drops by one; that assertion is updated in the same change, deliberately and visibly, rather than weakened.

## Related

- **LADR-01** — the engine being moved to.
- **LADR-05** — the guarantees the dropped table was providing.
