# LADR-06: Anchor lookups use property predicates, not inline maps

**Status:** Accepted

## Context

Every graph query in this design starts by finding a vertex from a memory's uuid. There are two ways
to write that in Cypher, they look interchangeable, and they are not.

The extension compiles an inline property map — `MATCH (n:Memory {memory_uuid: '…'})` — into an
agtype containment predicate, `properties @> '{"memory_uuid": "…"}'`. It compiles an explicit
predicate — `MATCH (n:Memory) WHERE n.memory_uuid = '…'` — into an equality test over the extracted
property, `agtype_access_operator(VARIADIC ARRAY[properties, '"memory_uuid"'])`. A btree index over
the extracted expression serves the second and cannot serve the first; a GIN index over the whole
properties map serves the first and is the wrong instrument for the second.

The cutover shipped the inline form everywhere, and the extension creates no property indexes of its
own — only `Memory_pkey` on the internal graph id, plus `LINKS_start_id_idx` and `LINKS_end_id_idx`
for edge traversal. So every anchor lookup in the store today is a sequential scan over the vertex
table:

```
Seq Scan on "Memory" s  (cost=0.00..75.51 rows=1 width=32)
  Filter: (properties @> '{"memory_uuid": "be07…"}'::agtype)
```

At three thousand vertices that is fast, which is the problem: wall-clock alone would report this as
passing NFR-02 while the access path guarantees it stops passing as the store grows. The measured
alternatives, on the same data:

| Form | Index | Plan | Cost |
|---|---|---|---|
| `WHERE n.memory_uuid = …` | btree over extracted property | `Index Scan` | 0.28..**8.30** |
| `{memory_uuid: …}` inline | GIN over `properties` | `Bitmap Index Scan` | 21.50..**25.52** |
| either, no index | — | `Seq Scan` | 0.00..**75.51** |

## Decision

**Write anchor lookups as explicit property predicates** — `MATCH (n:Memory) WHERE n.memory_uuid = …`
— and back them with a btree index over the extracted property.

`MERGE` is the exception and keeps the inline map, because a merge pattern *is* its match and cannot
be expressed as a `WHERE` clause. It is backed by a GIN index over `properties`.

Both indexes are therefore created deliberately, each serving one query form, in a migration that
states which is which.

## Alternatives Considered

- **GIN over `properties` alone, leaving the Cypher unchanged** — rejected: it does index the shipped form, but at three times the cost of a btree for what is a single-key equality test on an identity, and it indexes every key on the vertex when there is exactly one worth indexing.
- **Btree alone, accepting the sequential scan for `MERGE`** — rejected: `MERGE` is on the relationship write path, which runs a read-before-write on every link created (LADR-05). Leaving the one write path unindexed to save one index is a poor trade.
- **Store the uuid as a real column beside the graph storage and index that** — rejected: it is a second copy of the identity the vertex exists to hold, which is the dual-write failure LADR-02 and LADR-03 were written to prevent.
- **Accept sequential scans at current volume and revisit later** — rejected: NFR-02 requires the plan, not merely the time, precisely so this decision is made once with evidence rather than deferred until a slow query forces it.

## Consequences

- Two indexes exist on one table for two query forms. Anyone adding a graph query must know which form they are writing, so it is recorded in `AGENTS.md` Key Behaviors and `PERSISTENCE_AGENTS.md` rather than left to be rediscovered by `EXPLAIN`.
- The indexes are plain Postgres indexes over the extension's storage tables, so they are created by explicit SQL and are absent from the ORM's model snapshot — the same category as the cascade trigger the cutover added.
- Existing anchor lookups were rewritten as part of this change. The one-hop reverse lookup keeps its behaviour and gains an index access path, which is what makes NFR-02's baseline comparison a fair test rather than a comparison against an unindexed implementation.

## Related

- **LADR-01** — the extension whose compilation behaviour this decision responds to.
- **LADR-02** — why the uuid is not duplicated into a column to make indexing easier.
- **NFR-02** — the plan requirement this exists to satisfy.
