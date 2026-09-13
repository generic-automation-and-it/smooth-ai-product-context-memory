# LADR-01: Adopt Apache AGE in the existing Postgres for relationship storage

**Status:** Draft

## Context

Memory relationships are modelled as a relational table with a composite key and a reverse index. It
serves one-hop lookup well and variable-depth traversal badly. The provenance capability the store is
meant to provide — reconstructing the chain from a measurement to the decision it justified — is a
bounded path query.

Two families of option exist: a dedicated graph database alongside Postgres, or a graph capability
inside it. A separate server was assessed and rejected on consistency grounds: the write path already
depends on a single transaction spanning the version flip and insert, and a second store cannot join
that transaction.

Apache AGE supports Postgres 11 through 18, is Apache-2.0, and ships a published container image.

## Decision

**Adopt** Apache AGE as a Postgres extension and store memory relationships as graph edges within the
same database instance.

The database remains one instance, one connection pool, one backup and one transaction boundary. SQL
and Cypher execute against the same session, so a relationship write and the relational work around
it commit or roll back together. No additional service is deployed and the container count is
unchanged; only the database image differs.

Scope is deliberately narrow. AGE is introduced to answer path questions about memory relationships.
It is not adopted as a general modelling tool, and no existing relational concern moves to it.

## Alternatives Considered

- **A dedicated graph database alongside Postgres** — rejected: a second store cannot share the write transaction, and the design's core safety property is that a version flip and its insert are atomic.
- **Object-store-native distributed graph engines** — rejected: they solve storage/compute disaggregation at cluster scale, the inverse of a single-user local-first tool, and would still require a separate blob store for document bodies.
- **Recursive common table expressions over the existing table** — rejected as the long-term answer, though adequate today: they express bounded traversal but become unreadable for variable-depth queries with per-relation filtering, which is the target capability.
- **Keeping one-hop only and dropping the ambition** — rejected: relationship chains are the stated reason typed links carry a mandatory reason field.

## Consequences

- One engine, one transaction, one backup — the properties that make polyglot persistence expensive are avoided.
- Cypher and SQL compose in a single query, so a traversal can be filtered by relational predicates without an application-side join.
- The database image becomes a specific published image rather than the orchestrator's default, which couples upgrades to the extension's support matrix (see NFR-04).
- Schema is no longer fully described by the ORM's migration model; graph objects are created by explicit statements and are invisible to the model snapshot.
- The .NET driver situation is community-maintained rather than first-party, so relationship access is written against the database driver directly rather than through the ORM.

## Open

- Whether the published extension image or a derived image is used — resolved by NFR-03 verification during prototyping.

## Related

- **LADR-02** — constrains what may be stored on the graph side.
- **LADR-05** — handles the integrity guarantees this decision forfeits.
