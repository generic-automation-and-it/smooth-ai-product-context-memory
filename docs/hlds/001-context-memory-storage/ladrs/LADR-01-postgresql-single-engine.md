# LADR-01: PostgreSQL as one engine for relational and document data

**Status:** Accepted

## Context

The store needs relational constraints, range-typed validity, array containment, full-text search and
a home for evolving semi-structured metadata. A single-user local-first service can afford neither a
cluster nor several specialised stores, and write throughput is negligible.

An initial choice of SQLite was reversed once the requirement set grew to label arrays, temporal range
queries, ticket lookups and repository filters.

## Decision

**Adopt** PostgreSQL as the single database engine, used as both a relational and a document store,
accessed through EF Core with Npgsql.

The decisive factor was that Npgsql translates LINQ into server-side JSONB and array operators, so
document-shaped data stays queryable without hand-written SQL. One engine means one transaction
boundary, one backup and one connection pool.

Entity classes are dependency-free types in the domain layer; all mapping is fluent configuration in
infrastructure, so the domain carries no persistence concern.

## Alternatives Considered

- **SQLite (initially chosen, reversed)** — single-file simplicity and zero operations, but weaker for array containment, temporal range queries and composite lookups. *Still true from that analysis:* losing single-file portability makes backup and machine migration an explicit obligation rather than a free property.
- **A document database with a relational sidecar** — rejected: two stores cannot share one transaction, and the write path depends on atomicity across a version flip and an insert.
- **A graph database** — rejected at this stage: the measured access pattern is four parts filter to one part traverse, and filtering is index territory.

## Consequences

- One transaction spans relational, array and JSONB work; a partial write is impossible.
- Range types, GIN and GIST indexes, full-text search and generated columns are all available without extension.
- Backup covers every database concern in one operation — though not the object store (NFR-03).
- The engine is a second container alongside the API, accepted as standard rather than burdensome.

## Related

- **LADR-02** — how data is placed within this engine.
- **LADR-06** — what deliberately does not live here.
