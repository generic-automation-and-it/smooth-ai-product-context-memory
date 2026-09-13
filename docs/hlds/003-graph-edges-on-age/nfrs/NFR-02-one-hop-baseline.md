# NFR-02 — One-hop reverse lookup baseline (relational)

Recorded at HLD-003 foundation, **before** `memory_link` is dropped. The post-cutover verification compares the AGE one-hop against these figures. After the cutover the comparison cannot be reconstructed.

| | |
|---|---|
| **Date** | 2026-09-13 |
| **Store** | `memory_link` (relational), index `IX_memory_link_target_memory_id` |
| **Volume** | 3,000 memories, 10,000 relationships |
| **Query** | `SELECT source_memory_id, relation, reason FROM memory_link WHERE target_memory_id = $1` |
| **Warm-up** | 20 iterations discarded |
| **Measured** | 100 iterations |
| **p50** | 0.320 ms |
| **p95** | 0.429 ms |
| **NFR-02 one-hop target** | p95 ≤ 10 ms, and no slower than this baseline after cutover |
| **Hardware** | local Docker AGE image `docker.io/apache/age:release_PG17_1.7.0` (Postgres 17), Apple Silicon |
| **Re-run** | Relational one-hop harness removed at the AGE cutover (PR #38). Recorded numbers remain the baseline. **Post-cutover AGE re-run: 0.621 ms p95** — [NFR-02-traversal-measurements.md](./NFR-02-traversal-measurements.md). |

## Plan

```
Bitmap Heap Scan on memory_link  (cost=4.47..60.84 rows=24 width=122)
  Recheck Cond: (target_memory_id = 1501)
  ->  Bitmap Index Scan on "IX_memory_link_target_memory_id"  (cost=0.00..4.46 rows=24 width=0)
        Index Cond: (target_memory_id = 1501)
```

Access path is the reverse-lookup index, not a sequential scan.

Relation-type mix in the seed: most edges `relates_to` / `depends_on`, few `contradicts` / `supersedes` / `implements` (NFR-02).
