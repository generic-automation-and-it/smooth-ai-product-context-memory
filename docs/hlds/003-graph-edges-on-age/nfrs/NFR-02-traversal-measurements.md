# NFR-02 — Traversal measurements (post-cutover, AGE)

The three query shapes at target volume, with plans, plus the one-hop comparison against the
pre-cutover relational baseline in [NFR-02-one-hop-baseline.md](./NFR-02-one-hop-baseline.md).

| | |
|---|---|
| **Date** | 2026-09-13 |
| **Harness** | `tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest/Persistence/Nfr02BenchmarkTests.cs` |
| **Run** | `SMOOTH_AGE_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest --filter Nfr02BenchmarkTests` |
| **Volume** | 3,000 memories, 10,000 edges |
| **Relation mix** | `relates_to` 6,233 · `depends_on` 1,500 · `supersedes` 1,143 · `implements` 624 · `contradicts` 500 |
| **Warm-up** | 20 iterations discarded |
| **Measured** | 100 iterations per shape |
| **Image** | `docker.io/apache/age:release_PG17_1.7.0` (Postgres 17.11, AGE 1.7.0), Apple Silicon |
| **Measured through** | the shipped code path — `IMemoryTraversal.FindPathsAsync` and `IMemoryGraph.ListTouchingAsync`. Plans are `EXPLAIN` of the statement those methods build, not a hand-written copy |

## Results

| Shape | p50 (ms) | p95 (ms) | Target | Verdict |
|---|---|---|---|---|
| Depth-3 bounded path between two known identities, filtered by relation type | 0.869 | **1.057** | p95 ≤ 50 ms | **met** (47× margin) |
| One-hop reverse lookup | 0.549 | **0.616** | p95 ≤ 10 ms | **met** (16× margin) |
| Composed traversal plus relational filter | 8.217 | **8.522** | p95 ≤ 100 ms | **met** (12× margin) |

All three absolute targets are met with an order of magnitude to spare.

## The one-hop comparison

| | p95 |
|---|---|
| Relational `memory_link`, reverse-only, bitmap index scan (pre-cutover baseline) | 0.429 ms |
| AGE `:LINKS`, both directions, fully indexed (this run) | **0.616 ms** |
| Delta | **+0.187 ms — 1.4×** |

**On the strict reading of NFR-02 this is a regression, and NFR-02 says a regression blocks the
change.** The figure is recorded here rather than argued away. Three facts bear on how it should be
adjudicated, and none of them are a reason to call 0.595 ms "not slower than" 0.429 ms:

1. **It is not the same question.** The baseline query was `WHERE target_memory_id = $1` — inbound edges only. `ListTouchingAsync` returns inbound *and* outbound edges, which is two anchored index lookups unioned rather than one. The comparison is unfavourable to AGE by construction, because the graph implementation answers a strictly larger question. A reverse-only Cypher equivalent was not measured separately; that would be a fairer comparison and a less honest one, since it is not the method the store exposes.
2. **The residual is `cypher()` overhead, not access-path cost.** The plan (below) is index scans throughout — `ix_memory_vertex_uuid` for the anchor, AGE's own `LINKS_start_id_idx` / `LINKS_end_id_idx` for the hop, `Memory_pkey` for the far endpoint. What remains is the extension's own cost: parsing the Cypher, building `agtype` vertex and edge values, and rendering them to text for the driver. That cost is roughly constant, so the ratio narrows rather than widens as the store grows.
3. **The absolute figure is 16× inside the target.** 0.187 ms of added latency on a lookup budgeted at 10 ms.

The accepted multiple is now **asserted**, not merely recorded: the benchmark fails if the one-hop p95
exceeds three times the baseline. NFR-02 accepted 1.4×; it did not accept any multiple, and an
unasserted prediction that the ratio "narrows as the store grows" is a comment rather than a guard.

## Adjudication

**Decided 2026-09-13 by the HLD owner: accepted, not blocking.** NFR-02 moves to Accepted with this delta
on the record rather than removed from it.

The reasoning of record is the first point above: the criterion exists to stop us trading a working access
pattern for an unused one, and that is not what happened. The one-hop pattern was not traded away — it was
widened from reverse-only to bidirectional, and it remains fully indexed. The 166 µs is the extension's own
parse-and-materialise cost, which is constant, so the ratio narrows as the store grows.

What would reopen this: a measured one-hop above 10 ms, a plan that stops being index-only, or a growth
measurement showing the delta widening rather than narrowing. Any of those is a re-measurement, not a
re-argument.

## Shortfall found before tuning, and its cause

The first run missed badly, and it was the *plan* that caught it, not the clock — which is the reason
NFR-02 requires the plan at all.

| Shape | p95, first run | p95, after the fix |
|---|---|---|
| One-hop reverse lookup | **6.075 ms** (14.2× baseline) | **0.616 ms** (1.4× baseline) |
| Depth-3 bounded path | 0.982 ms | 1.057 ms |
| Composed traversal plus filter | 8.976 ms | 8.522 ms |

**Cause.** `ListTouchingAsync` asked for both directions with a disjunction over two *different* vertex
instances:

```cypher
MATCH (s:Memory)-[e:LINKS]->(t:Memory)
WHERE s.memory_uuid = $x OR t.memory_uuid = $x
```

No index on either endpoint can satisfy that `OR`, so Postgres hash-joined the entire edge table
against two full vertex scans and applied the predicate as a join filter:

```
Hash Join  (cost=211.00..506.81 rows=7 width=128)
  Join Filter: ((agtype_access_operator(... s.properties, '"memory_uuid"') = '"…"') OR (agtype_access_operator(... t.properties, '"memory_uuid"') = '"…"'))
  ->  Hash Join  (cost=105.50..374.78 rows=10000 width=151)
        ->  Seq Scan on "LINKS" e  (cost=0.00..243.00 rows=10000 width=83)
        ->  Hash  ->  Seq Scan on "Memory" s  (cost=0.00..68.00 rows=3000 width=68)
  ->  Hash  ->  Seq Scan on "Memory" t  (cost=0.00..68.00 rows=3000 width=68)
```

A sequential scan over **edge storage** — the access path NFR-02 names explicitly. Note that 6.075 ms
still passed the 10 ms target: wall-clock alone would have reported this as met, and it would have
stopped being met as the edge table grew.

**Fix, addressing the measured cause.** Two anchored matches unioned, so each branch anchors one
endpoint and uses the property index. `UNION` rather than `UNION ALL`, because a self-link satisfies
both branches and must still be reported once. Cost fell from 506.81 to 35.71 and p95 by 10×.

**A second change was tried and reverted.** Leaving the endpoint unlabelled (`(t)` rather than
`(t:Memory)`) when no target uuid is supplied, on the theory that the traversal would supply the
reachable set instead of the planner enumerating the label table. Measured effect: 8.976 → 8.786 ms,
inside run-to-run noise, and the plan gained an `Append` over `_ag_label_vertex`. It was reverted —
a branch in the query builder that buys nothing measurable is not worth keeping.

## Shortfall found in review, and its cause

Code review found that the scope rule gated the path's **endpoint** but not its **intermediate hops**, so
a path from a product-scoped memory through a programme-scoped one disclosed that memory's identity and
its edges' reasons — descriptive content on a read path, which this HLD's own non-negotiables forbid. An
L1 test reproduced the leak before the fix.

Gating intermediates cost more than expected on the first attempt:

| Composed shape | p95 |
|---|---|
| Before the gate existed (leaky) | 8.374 ms |
| Gate written as a join per path | **16.106 ms** |
| Gate written as a membership test against the hidden set | **8.522 ms** |

**Cause.** Joining `memory` inside the `NOT EXISTS` made the planner hash all 3,000 rows for *every*
candidate path: it estimates 100 rows from `jsonb_array_elements` and cannot know that a bounded path
holds four. Reshaped as a membership test against the set of memories in excluded scopes — computed
once, and a minority of the store — the correctness fix costs 0.15 ms instead of 7.7 ms.

The node list is converted to JSON by plain text replacement, which would be unsafe on the *edge* list:
a vertex carries `memory_uuid` and nothing else (LADR-02), so no caller-supplied string can appear in
its rendering, whereas an edge carries `reason`. That asymmetry follows from the edges-only decision and
is why the two lists are parsed by different means.

## Plans

### Depth-3 bounded path between two known identities, filtered by relation type

```
Limit  (cost=1.14..10.05 rows=50 width=651)
  ->  Nested Loop  (cost=1.14..60.50 rows=333 width=651)
        Join Filter: age_match_vle_terminal_edge(s.id, t.id, _age_default_alias_0.edges)
        ->  Nested Loop  (cost=1.14..26.34 rows=1 width=723)
              Join Filter: (m.group_id = g.id)
              ->  Nested Loop  (cost=1.14..25.32 rows=1 width=289)
                    ->  Nested Loop  (cost=0.86..24.93 rows=1 width=222)
                          ->  Nested Loop  (cost=0.56..16.61 rows=1 width=136)
                                ->  Index Scan using ix_memory_vertex_uuid on "Memory" s  (cost=0.28..8.30 rows=1 width=68)
                                      Index Cond: (agtype_access_operator(VARIADIC ARRAY[properties, '"memory_uuid"'::agtype]) = '"…"'::agtype)
                                ->  Index Scan using ix_memory_vertex_uuid on "Memory" t  (cost=0.28..8.30 rows=1 width=68)
                                      Index Cond: (agtype_access_operator(VARIADIC ARRAY[properties, '"memory_uuid"'::agtype]) = '"…"'::agtype)
                          ->  Index Scan using "IX_memory_uuid" on memory m  (cost=0.30..8.31 rows=1 width=86)
                    ->  Index Scan using "IX_memory_version_memory_id" on memory_version v  (cost=0.28..0.38 rows=1 width=83)
              ->  Seq Scan on memory_group g  (cost=0.00..1.01 rows=1 width=450)
        ->  Function Scan on age_vle _age_default_alias_0  (cost=0.01..10.01 rows=1000 width=32)
```

Both vertex anchors on `ix_memory_vertex_uuid`; path expansion through `age_vle`; the relational join
on `IX_memory_uuid` and `IX_memory_version_memory_id`. The `Seq Scan on memory_group` is a 1-page,
4-row table — an index there would be slower.

### One-hop reverse lookup

```
Subquery Scan on _  (cost=35.58..35.71 rows=6 width=128)
  ->  Unique  (cost=35.58..35.65 rows=6 width=128)
        ->  Sort  (cost=35.58..35.59 rows=6 width=128)
              ->  Append  (cost=0.85..35.50 rows=6 width=128)
                    ->  Nested Loop  (cost=0.85..17.71 rows=3 width=128)
                          ->  Nested Loop  (cost=0.57..16.67 rows=3 width=151)
                                ->  Index Scan using ix_memory_vertex_uuid on "Memory" s  (cost=0.28..8.30 rows=1 width=68)
                                ->  Index Scan using "LINKS_start_id_idx" on "LINKS" e  (cost=0.29..8.34 rows=3 width=83)
                          ->  Index Scan using "Memory_pkey" on "Memory" t  (cost=0.28..0.32 rows=1 width=68)
                    ->  Nested Loop  (cost=0.85..17.76 rows=3 width=128)
                          ->  Nested Loop  (cost=0.57..16.72 rows=3 width=151)
                                ->  Index Scan using ix_memory_vertex_uuid on "Memory" t_1  (cost=0.28..8.30 rows=1 width=68)
                                ->  Index Scan using "LINKS_end_id_idx" on "LINKS" e_1  (cost=0.29..8.39 rows=3 width=83)
                          ->  Index Scan using "Memory_pkey" on "Memory" s_1  (cost=0.28..0.32 rows=1 width=68)
```

Every access is an index scan. One branch per direction, each anchored on the property index and
walking edges through AGE's own `start_id` / `end_id` indexes.

### Composed traversal plus relational filter

```
Limit  (cost=0.59..5.12 rows=50 width=651)
  ->  Nested Loop  (cost=0.59..22655.01 rows=250000 width=651)
        Join Filter: age_match_vle_terminal_edge(s.id, t.id, _age_default_alias_0.edges)
        ->  Nested Loop  (cost=0.58..2628.14 rows=750 width=723)
              ->  Nested Loop  (cost=0.58..2615.88 rows=750 width=289)
                    ->  Nested Loop  (cost=0.30..1433.88 rows=3000 width=222)
                          ->  Nested Loop  (cost=0.00..181.00 rows=3000 width=136)
                                ->  Seq Scan on "Memory" s  (cost=0.00..83.00 rows=1 width=68)
                                ->  Seq Scan on "Memory" t  (cost=0.00..68.00 rows=3000 width=68)
                          ->  Index Scan using "IX_memory_uuid" on memory m  (cost=0.30..0.42 rows=1 width=86)
                    ->  Index Scan using "IX_memory_version_memory_id" on memory_version v  (cost=0.28..0.38 rows=1 width=83)
                          Filter: ((kind)::text = 'decision'::text)
              ->  Materialize  ->  Seq Scan on memory_group g  (cost=0.00..1.01 rows=1 width=450)
        ->  Memoize  (cost=0.01..10.02 rows=1000 width=32)
              Cache Key: s.id
              ->  Function Scan on age_vle _age_default_alias_0  (cost=0.01..10.01 rows=1000 width=32)
```

**This shape scans the vertex table, deliberately.** It asks for everything reachable from one anchor
within the bound, so there is no second endpoint to index against — the endpoint set genuinely is every
vertex until the terminal-edge filter narrows it. Two things make that acceptable rather than a
shortfall:

- **Edge storage is not sequentially scanned.** `LINKS` is reached only through `age_vle`, which is the criterion NFR-02 states.
- **The cost is bounded by the depth limit, not by the table.** This is the shape LADR-07 exists for: the bound, not an index, is what stops it.

It is the slowest of the three at 8.374 ms and still 12× inside its target. If it becomes the dominant
access pattern, the thing to revisit is the endpoint enumeration, and the fix would be measured then —
not guessed at now.

## What was not measured

- **Whether the intermediate-hop gate stays cheap as the excluded set grows.** The hidden set is a minority of the store today; if programme-scoped memories became the majority, the membership test needs re-measuring.
- **A reverse-only AGE one-hop**, which would compare more favourably against the reverse-only baseline. Deliberately omitted: it is not the method the store exposes, so measuring it would flatter the comparison rather than inform it.
- **Growth beyond 10,000 edges.** The target volume is roughly an order of magnitude past the expected first-year store, and the plans are index- and traversal-based, so the shapes should degrade with the log of the table rather than its size. That is a prediction from the plan, not a measurement.
- **Concurrency.** The service is single-user and local-first; contention is not a modelled concern.
