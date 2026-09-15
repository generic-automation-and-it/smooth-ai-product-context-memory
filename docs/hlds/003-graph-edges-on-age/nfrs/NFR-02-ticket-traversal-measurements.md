# NFR-02: Final ticket traversal evidence

**Status:** Accepted, 2026-09-15. All four explicit benchmark cases passed; the ticket performance
gate is satisfied without widening any threshold. This records local release acceptance, not publication.

The original pre-merge run, tables and acceptance below are preserved as historical evidence.
The [2026-09-15 review revalidation](#2026-09-15-review-revalidation) records later fixes and failures
separately. Final full-suite repeat also passed: 429 tests, four intentionally gated benchmark skips.

## Run and Method

| Item | Evidence |
|---|---|
| Source | `ticket-final-all-benchmarks.trx`, run `55d79ce4-8b8c-4e69-9401-da992857b24c`, 2026-09-15; counters: 4 executed, 4 passed, 0 failed, 0 skipped |
| Harness | `TicketTraversalBenchmarkTests` (three fanouts) and original `Nfr02BenchmarkTests` (one case) in `Infrastructure.ComponentTest/Persistence/` |
| Reproduce | `SMOOTH_AGE_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest --filter "FullyQualifiedName~TicketTraversalBenchmarkTests\|FullyQualifiedName~Nfr02BenchmarkTests"` |
| Environment | Existing pinned `docker.io/apache/age:release_PG17_1.7.0`, PostgreSQL 17.11 / AGE 1.7.0, local Apple Silicon; TRX host `M-05433`, Debug/net10.0, runtime .NET 10.0.9, xUnit VSTest adapter 3.1.5 |
| Environment provenance | Image/version/hardware pairing retained from [existing environment record](./NFR-02-traversal-measurements.md); TRX does not inventory CPU model or RAM |
| Sampling | 20 discarded warm-ups, 100 measured calls per shape; p50 averages sorted samples 49/50, p95 interpolates samples 94/95 (zero-based) |
| Measured boundary | Actual `NpgsqlTicketGraph.TraverseAsync`, including materialization; EXPLAIN ANALYZE/BUFFERS on `CreateTraversalCommandAsync`, not a handwritten query |
| Query | Outbound; `kind=decision`, required scope `product`, hidden dimensions `program`; path/memory limits 200 each; live ownership and hop gates active |

Each group owns one ticket and contains three current memories. Every root child has a grandchild and leaf:
**three actual edges deep**, not five. Child scopes mix product/customer/program; some leaves are
program-scoped. Requested `maxDepth=5` tests the supported maximum on this three-deep hierarchy;
it does **not** benchmark a five-deep chain. At larger fanouts the response cap selects depth-one
paths while the recursive walk still expands deeper visible branches. Only selected capped path
endpoint groups plus the anchor contribute memories.

## Ticket Results

All times are milliseconds. Groups and tickets have identical counts in this fixture.

| Fanout | Groups / Tickets | Edges | Memories | Requested Depth | p50 | p95 | Returned Paths | Verdict |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| 100 | 301 | 300 | 903 | 2 | 13.612 | 14.406 | 160 | Pass |
| 100 | 301 | 300 | 903 | 3 | 20.374 | 22.826 | 200 | Pass |
| 100 | 301 | 300 | 903 | 5 | 40.510 | 55.884 | 200 | Pass |
| 500 | 1,501 | 1,500 | 4,503 | 2 | 15.271 | 22.090 | 200 | Pass |
| 500 | 1,501 | 1,500 | 4,503 | 3 | 23.294 | 32.387 | 200 | Pass |
| 500 | 1,501 | 1,500 | 4,503 | 5 | 14.194 | 59.092 | 200 | Pass |
| 1,000 | 3,001 | 3,000 | 9,003 | 2 | 25.206 | 33.399 | 200 | Pass |
| 1,000 | 3,001 | 3,000 | 9,003 | 3 | 22.193 | 28.544 | 200 | Pass |
| 1,000 | 3,001 | 3,000 | 9,003 | 5 | 22.565 | 28.044 | 200 | Pass |

Every row returns 200 memories and reports the memory cap reached. The path cap is reached except
fanout 100/depth 2. The depth flag is true only for requested depth 2, where a further visible hop
exists. Generic upstream coverage/freshness disclosure is present in every recorded result.
Worst p95 is **59.092 ms**, below the unchanged **100 ms** target.

## Memory Regression Check

Original fixture: 3,000 memories / 10,000 `LINKS`; relation counts are 6,233 relates_to, 1,500 depends_on,
1,143 supersedes, 624 implements and 500 contradicts. Same final TRX, all three shapes passed:

| Shape | p50 (ms) | p95 (ms) | p95 Budget (ms) |
|---|---:|---:|---:|
| Depth-3 bounded path, relation-filtered | 0.811 | 1.141 | 50 |
| One-hop reverse lookup | 0.532 | 0.590 | 10 |
| Composed traversal and relational filter | 12.614 | 13.060 | 100 |

One-hop remains 1.4x the 0.429 ms relational baseline (+0.161 ms), not literally faster. The
[existing adjudication and 4x guard](./NFR-02-traversal-measurements.md#the-one-hop-comparison)
remain unchanged; this ticket change widens neither that guard nor any absolute budget.

## Actual Plan Evidence

The ticket SQL composes a Cypher anchor with recursive SQL over AGE adjacency, not variable-length
Cypher. Short excerpts below are from fanout 1,000/requested depth 5 (TRX lines 1193-1293); omitted
plan branches are not replaced with invented measurements.

| Plan excerpt | Observed work |
|---|---|
| `CTE identities -> Seq Scan on "Ticket"` | 3,001 rows, one scan; parse identity properties once |
| `CTE owners -> HashAggregate -> Merge Join` | Join 3,001 memberships; 2,658 visible unique owners; `count(*) = 1` and nonempty `program` gate applied |
| `Sort Key: g_1.provider COLLATE "C", g_1.key COLLATE "C"` | Exact ordinal identity join, not normalization |
| `Index Scan using ix_ticket_vertex_key on "Ticket"` | Root key lookup; provider equality retained as filter |
| `CTE walk -> Recursive Union` | 2,287 rows including anchor; four recursion rounds, actual multi-hop expansion |
| `Index Scan using "TICKET_PARENT_start_id_idx" ... Index Cond: (start_id = w.id)` | 2,287 indexed adjacency probes; no sequential scan of ticket edge storage |
| `Sort Key: admitted.depth, admitted.sort_key COLLATE "C", admitted.edges` | 2,286 admitted paths; deterministic top-N selection of 200 |
| `PK_memory_group`, `IX_memory_group_id_subject_slug`, `IX_memory_version_memory_id` | Relational owner/memory/version joins; 268 eligible memories before the independent cap of 200 |

That EXPLAIN reports planning **0.408 ms**, execution **29.829 ms**. It is a separate plan sample,
not the timing-series p95. Sequential scans over identities/memberships are intentional live joins;
the edge-storage access remains indexed. The migration also supplies provider HASH and properties
GIN indexes; this read selected the **key HASH** index, not all indexes at once. GIN serves MERGE.
The outbound benchmark does not claim to exercise the inbound adjacency index.

The original memory plans retain `ix_memory_vertex_uuid`, `LINKS_start_id_idx` / `LINKS_end_id_idx`
and `Memory_pkey` for one-hop, and `Function Scan on age_vle` for variable-depth memory expansion.
The composed memory plan still contains vertex/relational scans; no blanket index-only claim is made.

## Corrections and Limits

Initial failures exposed quadratic ownership joins and repeated identity parsing. Materializing
identities once and using ordinal `C` collation on exact provider/key joins fixed the measured query
shape; the final plan shows a merge join rather than repeated all-pairs identity work. Neither
visibility gates nor caps were bypassed to pass. The export fixture's five groups also reused one
ticket; it now gives each group a unique exact identity, rather than weakening ownership enforcement.

Local machine sleep/scheduling noise affected elapsed-run interpretation. TRX wall-clock spans differ
from test durations, and timings are not monotonic by dataset size. Preserve the actual samples above;
do not derive scaling guarantees from them or use sleep noise to excuse a failed budget. No threshold
was widened. This is a local warm-run benchmark, not a cold-start, concurrent-load or five-deep result.

## Final Acceptance

Benchmark values and counters above were read directly from the final TRX. The following full-suite,
build and review outcomes were supplied by the completing run; this documentation pass did not rerun them.

| Check | Result |
|---|---|
| AppHost / Application unit / Domain unit / Host unit / Infrastructure unit | 5 / 117 / 32 / 17 / 29 passed |
| Application component / Infrastructure component / Host integration | 36 / 109 / 51 passed |
| Normal solution total | **396 passed**, 4 intentionally gated benchmark skips, 0 failures |
| Explicit benchmarks | **4 passed**, 0 skipped, including original memory case and all ticket fanouts |
| Python skill harness | **34 passed** |
| Build | 0 warnings, 0 errors |
| Final review | No runtime findings after fixes |

Standing scope, mutation, migration and lifecycle tests passed in the final suite. Ticket-only Down
still **warns that captured hierarchy declarations are lost**; it preserves Memory vertices, memory
`LINKS` and relational metadata. Reapplying restores identities, not lost declarations. LADR-02's
pre-migration supersession remains recorded. Acceptance covers the ticket graph and evidence-only
near-miss helper, not HLD-005's full dossier or blocked tag identity/synonym decisions.

## 2026-09-15 Review Revalidation

**Benchmark result: 4 executed, 4 passed, 0 failed, 0 skipped after the fixes.** Source:
`ticket-surgical-all-benchmarks.trx`, run `15e26526-5f40-4c79-9d6e-d5c47560d8ee`,
08:49:02-08:51:42 +02:00 on `M-05433` (TRX lines 2-3, 2488). Local source directory:
`/var/folders/v6/vkxf0ng17z54v20yr5kjypv00000gn/T/opencode/`. Values and plan facts are transcribed
here so the evidence does not depend on retaining temporary files. Current package baseline is
Microsoft **10.0.11** and Aspire **13.5.3** (`Directory.Packages.props`), not a new runtime-version
claim. The pinned PostgreSQL/AGE pairing, fixture, active scope gates, caps, 20 warm-ups and 100 samples
per shape remain as above. Requested depth 5 still uses a three-deep hierarchy.

### Exact benchmark results

Ticket values below are verbatim numeric values from the final TRX, sorted by fanout/depth; all times
are milliseconds. Each row returns 200 memories. Depth cap is true only at depth 2; path cap is false
only at fanout 100/depth 2; memory cap is true throughout. Generic coverage/freshness disclosure remains.

| Fanout | Groups / Tickets | Edges | Memories | Requested Depth | p50 | p95 | Returned Paths | TRX Line |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 100 | 301 | 300 | 903 | 2 | 10.049 | 24.188 | 160 | 1682 |
| 100 | 301 | 300 | 903 | 3 | 11.038 | 18.006 | 200 | 1941 |
| 100 | 301 | 300 | 903 | 5 | 10.923 | 12.118 | 200 | 2199 |
| 500 | 1,501 | 1,500 | 4,503 | 2 | 28.810 | 34.797 | 200 | 12 |
| 500 | 1,501 | 1,500 | 4,503 | 3 | 26.083 | 28.115 | 200 | 275 |
| 500 | 1,501 | 1,500 | 4,503 | 5 | 28.004 | 31.703 | 200 | 537 |
| 1,000 | 3,001 | 3,000 | 9,003 | 2 | 24.699 | 27.836 | 200 | 899 |
| 1,000 | 3,001 | 3,000 | 9,003 | 3 | 17.478 | 26.973 | 200 | 1159 |
| 1,000 | 3,001 | 3,000 | 9,003 | 5 | 17.291 | 18.413 | 200 | 1418 |

All nine shapes pass the unchanged **100 ms** ticket budget; worst p95 is **34.797 ms**.
Original memory case also passes; its raw table is retained exactly from TRX lines 806-810:

| Shape | p50 (ms) | p95 (ms) | Target (ms) | Verdict |
|---|---|---|---|---|
| depth-3 bounded path, filtered by relation type | 1.459 | 1.756 | 50 | met |
| one-hop reverse lookup | 0.942 | 1.088 | 10 | met |
| composed traversal plus relational filter | 31.867 | 61.238 | 100 | met |

The TRX explicitly labels one-hop **REGRESSION against the baseline**: 1.088 ms versus 0.429 ms,
2.5x rounded (+0.659 ms). It passes the existing **4x** guard (1.716 ms) and 10 ms absolute budget;
do not describe it as faster or erase that baseline regression. No threshold changed.

### Failed attempts and query correction

Earlier loop-3 failures remain failures, not successful revalidation or discarded scheduling noise:

| Earlier TRX | Passed / Failed | Failed ticket shapes: fanout/depth and p95 (ms) |
|---|---:|---|
| `ticket-review-loop3-final.trx` | 3 / 1 | 1,000/2: 105.499; 1,000/3: 106.013 |
| `ticket-review-loop3-verified.trx` | 24 / 1 | 1,000/2: 434.791; 1,000/3: 131.819 |
| `ticket-review-loop3-indexed.trx` | 23 / 2 | 1,000/2: 111.446; 1,000/3: 407.014; 1,000/5: 111.132; 500/3: 106.090; 500/5: 235.022 |
| `ticket-review-loop3-edge-index.trx` | 24 / 1 | 1,000/2: 226.552; 1,000/3: 105.343 |

Stricter JSON membership guards lowered the owner estimate; the filtered aggregate estimated one
owner despite thousands of eligible rows, allowing reversed expansion/repeated owner work. Intermediate
query-local owner-map and index attempts did not close the gate. The current provider instead keeps
the owner aggregate unfiltered and uses `CASE WHEN count(*) = 1 AND bool_and(...) THEN t.id END`:
ineligible IDs become NULL and cannot join the anchor or frontier. This preserves eligibility without
the extra `HAVING` selectivity collapse. `OFFSET 0` keeps adjacency probes anchored to each frontier
vertex and the owner join outside that per-vertex subquery, once per recursive level. Hidden or
ambiguous destinations never enter the next frontier, path selection or cap counts.

The walk carries IDs, sort keys and the live endpoint group ID, not growing hop JSON. Only after the
deterministic path cap does it hydrate ordered declaration metadata via
`ix_ticket_parent_id`, a btree on `TICKET_PARENT(id)` added by `AddTicketGraph`. That index serves
post-cap hydration, not adjacency expansion; AGE start/end indexes still serve expansion. There is
**no persisted ownership cache** or extra query: ownership, visibility, selection and hydration use
the **same SQL statement snapshot**. Materialized CTEs are statement-local, not another source of truth.

Final fanout 1,000/depth 5 plan (TRX lines 1516-1675) shows owners estimated 225/actual 3,001 aggregate
rows, a frontier hash join over three nonempty levels, 2,287 `TICKET_PARENT_start_id_idx` probes,
2,286 admitted paths, 200 selected paths and 200 `ix_ticket_parent_id` hydration probes. The aggregate
includes NULL-ID ineligible rows; it does not admit 3,001 visible owners. `Ticket_pkey` hydrates the
directed endpoints. Planning is **0.401 ms**, execution **18.048 ms**, separate from timing-series p95.
The outbound run does not establish inbound-index performance, cold-start or concurrent-load behavior.

### Three-loop review and checks

Across the three review/fix loops, the delivered corrections were:

- Transaction integrity: explicit EF `ReadCommitted` only; stale snapshots rejected; own transaction
  or private savepoint with uncancelled recovery; one-command reparent and exact vertex/result
  cardinality. Migration advisory-before-table `NOWAIT` fails `55P03` for quiesce/retry, not deadlock.
- Corruption boundary: typed string memberships, invalid containers absent; selected stored JSON
  failures become sanitized 500 without parser details, while hidden-only corruption leaves responses
  unchanged. Scope/cap semantics remain intact through the measured query correction above.
- Skill and test correctness: wire-compatible UTF-16/timestamp guards; near-miss evidence retains
  `originalQuery.facetMatchMode`, per-record status/scope and explicit proposed evidence without
  changing selection/disclosure. Cross-store trace assertions select a supplied W3C trace ID and
  verify parentage, with a later same-route decoy, rather than choosing the latest route match.

| Check | Recorded result and provenance |
|---|---|
| Earlier full solution | **429 passed, 4 gated benchmark skips**, supplied by the main run; predates the final query revalidation |
| Earlier focused checks | **76 passed**, supplied by the completing review loop |
| Python skill harness | **42 passed**, supplied by the main run |
| Full Host integration | **52 passed in each of three sequential runs**, supplied by the main run; no test-parallelism weakening |
| Latest focused traversal checks | **21 passed, 0 failed**, `ticket-surgical-attempt2-functional.trx`, run `cdd4030c-ba3d-4154-bdea-e0ae60f4a157`, counters line 144 |
| Explicit final benchmarks | **4 passed, 0 failed, 0 skipped**, directly read from `ticket-surgical-all-benchmarks.trx` |
| Final full-suite repeat after the query fixes | **429 passed, 4 gated benchmark skips**, zero failures; `dotnet test SmoothAiProductContextMemory.slnx --no-build -m:1` |

The first post-optimization full-suite repeat encountered a database connection timeout during a
benchmark fixture's initialization, before its environment-gated skip. All 429 functional cases passed
in that run; the subsequent complete repeat passed with all four expected skips. This transient setup
failure is retained here rather than represented as an application failure or silently omitted.

This pass read code and existing evidence only; it ran no tests. The original acceptance record is
historical, and these checks do not imply full dossier implementation or unblock tag identity/synonyms.
