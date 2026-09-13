# LADR-07: Three enforcement tiers — constraint, mechanism, verified-soft

**Status:** Accepted

## Context

Some guarantees this design depends on can be database constraints. Some cannot be expressed as
constraints but can still be enforced by something other than good intentions. A few cannot be
enforced at all and are genuinely behavioural.

Leaving that distinction implicit is how a design quietly becomes conventions in prose. Prose gets
skimmed, and the rules most likely to be violated are the ones that look like unnecessary indirection.

## Decision

**Classify** every guarantee into one of three tiers, and state which tier each occupies.

**Tier 1 — database constraint.** Exactly one current version per memory (partial unique index).
Version chain integrity. Identity uniqueness. Initiative always assigned, via a not-null reference to
a seeded sentinel — a sentinel rather than null because null cannot be searched, grouped or
constrained, and a default value additionally yields a triage inbox of unassigned work.

**Tier 2 — enforced by mechanism.** Guarantees no constraint expresses, given a mechanism that fails
loudly rather than a rule that is remembered:

- *History is append-only* — a trigger rejects update and delete on both history tables. A deliberate bypass exists for cascade deletion and must be transaction-scoped, never session-scoped: a session-scoped setting outlives the operation on a pooled connection and hands a later unrelated caller permission to delete history.
- *Denormalised concepts stay denormalised* — a guard test asserts the exact entity set, so reintroducing a removed table fails the build.
- *Classification stays unversioned* — a test adds tags, bumps the version, and asserts they neither duplicate nor version.
- *The registry stays advisory* — a test asserts a facet absent from the registry is accepted, since an enforcing registry would contradict the open-vocabulary design.

**Tier 3 — verified-soft.** Subject uniqueness cannot be a constraint at all: *"PostgreSQL is the
storage engine"* and *"we store in Postgres"* are the same subject with different strings, and semantic
equivalence is not expressible in SQL. Ticket-to-group uniqueness became soft when tickets were
denormalised. Both are labelled soft, both are enforced by the write path, and both are checked in the
same read-before-write pass it already performs.

## Alternatives Considered

- **Documenting all of it as convention** — rejected: prose is skimmed, and these are precisely the rules that look like unnecessary indirection to a reader without the context.
- **Enforcing ticket uniqueness in-database over the denormalised array** — rejected: would require an exclusion constraint with no clean operator for text arrays, forcing a hash-to-integer workaround for a check the write path performs anyway.
- **Adding a foreign key from facets to the registry** — rejected: would make an explicitly advisory registry enforcing, contradicting the design.

## Consequences

- Which guarantees are structural and which are behavioural is written down, so neither is mistaken for the other.
- Tier-2 mechanisms fail loudly. **The anticipated failure mode is not ignoring a warning but relaxing a red assertion during unrelated work** — so the mechanisms are documented as load-bearing, and weakening one is a defect rather than a fix.
- Tier-3 guarantees depend on the write path behaving correctly, which is a real and named risk rather than an assumed property.
- The tier list must be updated when a guarantee moves between tiers, or it becomes the prose it replaced.

## Related

- **LADR-02** — the denormalisation that moved ticket uniqueness to tier 3.
- **NFR-01** — the verification that proves each tier holds.
