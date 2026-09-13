# NFR-03 — Restore verification (relational rows and graph edges)

Recorded post-cutover, with a populated graph. This is the check NFR-03 singles out as the one most
likely to be skipped and the most expensive to skip: a backup that silently omits graph data looks
entirely successful until the first traversal after a restore.

| | |
|---|---|
| **Date** | 2026-09-13 |
| **Script** | [`scripts/verify-graph-restore.sh`](../../../../scripts/verify-graph-restore.sh) |
| **Fixture** | [`scripts/seed-graph-sample.sh`](../../../../scripts/seed-graph-sample.sh) — 200 memories, 200 vertices, 500 edges in the NFR-02 relation-type distribution |
| **Image** | `docker.io/apache/age:release_PG17_1.7.0` (Postgres 17.11, AGE 1.7.0) |
| **Method** | `pg_dump -Fc` of the populated database, `pg_restore` into a **freshly created empty** database in the same instance, then counts and a traversal against the restored copy |
| **Result** | **PASSED** |

## Measured

```
== source: nfr03_source
   memory rows: 201 | vertices: 200 | edges: 500
== backup (custom format, single dump covering both models)
   /tmp/restore_check_1789317379.dump (65006 bytes)
== restore into a fresh, empty database: restore_check_1789317379
   memory rows: 201 | vertices: 200 | edges: 500
== traversal against the restored database
   depth-1..2 paths found: 1797
OK:   relational row count (memory) matches (201)
OK:   graph vertex count matches (200)
OK:   graph edge count matches (500)
== NFR-03 restore verification PASSED
```

## What this establishes

- **One backup covers both models.** A single `pg_dump` of the database carried the relational rows and the graph edges together. No separate graph export step exists or is needed.
- **The edge count after restore is non-zero and matches the source** — 500, not 0. This is the specific failure NFR-03 asks the check to catch.
- **Traversal works on the restored copy without recreating anything.** 1,797 depth-1..2 paths, from a database that was empty moments earlier.
- **Container count is unchanged.** The restore target is another database inside the same Postgres container; nothing was deployed to make the graph restorable.

## Why the counts are read from AGE's storage tables

`memory_graph."LINKS"` and `memory_graph."Memory"` are counted directly rather than through `cypher()`.
The question is whether the *rows survived the dump*, and a plain `count(*)` cannot be confused by
session setup — a Cypher count that returned zero would leave "did the restore drop the edges?"
and "did this session forget to `LOAD 'age'`?" indistinguishable, which is the ambiguity this check
exists to remove.

## Repeating it

```bash
scripts/seed-graph-sample.sh nfr03_sample          # populate a scratch database
scripts/verify-graph-restore.sh mimisbrunnr-postgres nfr03_sample
```

The script refuses to pass against a source with zero edges: an edge count of zero matches zero, and a
green check on an empty graph is worse than no check at all.
