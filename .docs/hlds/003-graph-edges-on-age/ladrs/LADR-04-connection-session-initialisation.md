# LADR-04: Initialise the AGE session per physical connection

**Status:** Draft

## Context

The extension requires each session to load its library and place its catalog schema on the search
path before any graph statement will parse. This is per *session*, not per database — a correctly
installed extension still fails on a connection that has not been prepared.

The service uses a pooled data source. A pool hands out physical connections that outlive a single
request, so preparation performed once at start-up applies to whichever connection happened to be
open at the time and to no other. The failure this produces is intermittent and load-dependent: the
same query succeeds or fails depending on which connection serves it.

A second hazard is transactional. The extension's catalog-writing functions — creating a graph or a
label — are ordinary writes and are invisible to other sessions until committed. Migration tooling
wraps statements in a transaction by default, and a nested scope that looks like a transaction may
only be a savepoint, which does not commit.

## Decision

**Initialise** the AGE session on every physical connection at the point the pool opens it, using the
data source's physical-connection initialisation hook rather than any per-request or per-start-up
path.

Schema creation — the extension, the graph, and the vertex and edge labels — runs in migration
statements that are explicitly not wrapped in the migration's ambient transaction, so the catalog
writes commit and become visible to subsequent sessions.

Both are verified rather than assumed. A test that exhausts and recycles the pool proves the
initialiser runs for every connection, not merely the first.

## Alternatives Considered

- **Initialise once at application start-up** — rejected: prepares one connection, leaving the rest of the pool to fail intermittently under load.
- **Prefix every graph statement with the preparation statements** — rejected: works, but repeats setup on every call and makes composed SQL-and-Cypher queries awkward to author.
- **Disable pooling** — rejected: trades a bounded setup concern for a connection-per-request cost.
- **Bake preparation into the database image** — rejected: the search path is a session property; an image cannot set it for a client session.

## Consequences

- Graph access works uniformly regardless of which pooled connection serves a request.
- The data source acquires a required construction step; a code path that builds a connection without it will fail on first graph use, which the pool-recycling test is designed to catch.
- Migrations gain a category of statement that must not be transaction-wrapped, which is unusual enough to warrant an explicit comment where it occurs.
- Ordering matters at install time: the extension must be created before other roles are granted schema-creation rights, or its catalog schema can be pre-created under the wrong owner and the install refuses.

## Open

- Whether the initialiser is sufficient for tooling that opens its own connections outside the data source — resolved during prototyping by running the migration and the export path against a recycled pool.

## Related

- **LADR-01** — introduces the extension this prepares.
