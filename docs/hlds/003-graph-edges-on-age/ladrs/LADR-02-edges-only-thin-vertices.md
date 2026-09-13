# LADR-02: Vertices carry identity only; all properties stay relational

**Status:** Draft

## Context

Once a graph exists, the cheapest-looking move is to copy a few descriptive fields onto vertices so a
traversal can filter without joining back. Subject, kind and scope are the obvious candidates.

Every polyglot design that has failed in the prior art surveyed failed the same way: the same fact
existed in two stores, the two drifted, and no mechanism said which was authoritative. The
persistence design already treats derived-versus-stored as a first-class question — usage counts are
a view precisely because a maintained counter drifts and a derived one cannot.

## Decision

**Constrain** graph vertices to a label and the memory's stable identity. No descriptive property is
stored on a vertex.

A vertex is an anchor, not a record. Traversal returns identities; any question about what those
memories say is answered by selecting the corresponding relational rows. Because SQL and Cypher share
a session, that is one composed query rather than an application-side round trip.

The rule is testable in the strongest form: asserting that a vertex has no property beyond identity
is a passing test today and a failing test the moment someone adds one. That converts a design
principle into a build-breaking guarantee, in the same way the seven-entity model-shape guard already
does for the relational side.

## Alternatives Considered

- **Denormalise the filter fields onto vertices** — rejected: creates two copies of one truth with no arbiter, which is the exact failure mode being designed against.
- **Store everything in the graph and retire the tables** — rejected: forfeits the partial unique index, the append-only trigger, range-typed validity and full-text search, converting four hard guarantees into soft ones.
- **Mirror properties but mark the graph read-only** — rejected: a read-only copy still has to be rebuilt, and a rebuild path is a sync product.

## Consequences

- The graph cannot drift from the relational store, because it holds nothing that could drift.
- Adding or changing a memory property requires no graph change at all.
- Traversals that filter on descriptive fields must compose with a relational predicate rather than filtering purely in Cypher, which is a modest query-authoring cost.
- Graph-side storage stays small, keeping traversal working over identities rather than payloads.

## Related

- **LADR-01** — establishes the engine this constrains.
- **LADR-03** — the relationship table is replaced, not mirrored, for the same reason.
