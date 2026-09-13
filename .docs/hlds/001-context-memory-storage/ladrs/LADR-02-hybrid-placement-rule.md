# LADR-02: Placement rule — typed columns for hot filters, JSONB for the long tail

**Status:** Accepted

## Context

A first model used thirteen entities, with separate tables for tickets, repositories, tags, facets and
sources. Reviewing it raised whether those relations earned their complexity. For a single-user local
service, **schema complexity is a real cost and performance is not** — which inverts the usual
normalisation trade-off and makes read-then-write on a denormalised document perfectly acceptable.

The opposite error is equally available: putting everything in JSONB because it is convenient, then
discovering that a field filtered on every request has no index.

## Decision

**Adopt** a placement rule applied per concept rather than by habit.

Denormalise into columns, arrays or JSONB when **all** hold: it is a value rather than an entity with
its own lifecycle; it is only ever read alongside its parent; no hard constraint depends on it; and it
is not append-only under concurrency.

Keep it relational when **any** hold: it has independent state; it requires bidirectional traversal;
it is append-only history; or a constraint genuinely relied upon depends on it.

Applying the rule removed six of thirteen entities. Tags and facets became indexed arrays; sources and
tickets became JSONB; repository became plain columns. Registries with their own lifecycle, graph
edges needing reverse traversal, and append-only history stayed relational.

Every JSONB object carries a **shape marker** as its first key. This is per-document rather than
per-column because collections accumulate over time — elements are appended under whatever shape was
current when each was written, so a column-level version would claim one shape for a row holding two.
Retrofitting is impossible once unmarked documents exist.

## Alternatives Considered

- **Full normalisation (the original thirteen entities)** — rejected: the constraints those tables bought were unneeded, actively contrary to the design, or replaceable at zero cost by a check the write path already performs.
- **Full denormalisation, including history as JSONB arrays** — rejected: appending to a JSONB array is read-modify-write, so concurrent appends silently lose one. History is the audit trail.
- **Maintained counters for label usage** — rejected in favour of a derived view. A maintained counter drifts; a derived one cannot.

## Consequences

- Seven entities instead of thirteen; three foreign keys on the main path.
- Common reads need no joins — a memory carries its own tags and facets, a group its tickets and repository.
- Renaming a repository or initiative touches many rows, accepted because writes are rare.
- **JSONB shape drift is the principal residual risk.** A table change gets a migration; a shape change inside a document gets nothing. The shape marker plus strict serialisation through one typed model is the mitigation.
- JSONB predicates are less discoverable than joins for anyone reading the schema cold, which the diagrams and context files compensate for.

## Related

- **LADR-01** — the engine this places data within.
- **LADR-07** — what happens to constraints the denormalised concepts used to carry.
