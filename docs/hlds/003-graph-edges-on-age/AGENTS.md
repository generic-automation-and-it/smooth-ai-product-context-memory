# AGENTS.md - Graph edges on Apache AGE

AI Context: HLD for graph edges on Apache AGE. Updated: 2026-09-13

## TL;DR

Memory relationships move from a relational table to graph edges inside the same PostgreSQL instance
via the Apache AGE extension. Intent and goals in [README.md](./README.md); decisions in
[./ladrs/](./ladrs/); measurable quality bar in [./nfrs/](./nfrs/); boundary and flows in
[./diagrams/c4-context.md](./diagrams/c4-context.md). The business requirement this design exists to
satisfy is BR-11 in [BRD 001](../../brd/001-context-memory/) — related knowledge is connected, and the
connection's reason is recorded. The reason is not optional decoration: it is half of what BR-11 asks
for.

## Non-Negotiables

- **An edge without a recorded reason does not satisfy BR-11.** The BRD's case for relationships is the chain "a measurement produced a finding, which justified a decision" — an untyped, unexplained edge preserves adjacency and loses the chain.
- **Never put a descriptive property on a vertex.** A vertex holds the memory's identity and nothing else. Subject, claim, scope, kind, validity and tags stay relational (LADR-02). A vertex with a second descriptive attribute is the failure this design exists to avoid.
- **Never write a relationship to both a table and the graph.** There is one representation, not two with a reconciler (LADR-03).
- **Never delete a memory without removing its edges in the same transaction.** No foreign key cascades into the graph; a two-statement delete outside one transaction can strand orphan edges (LADR-05).
- **Never create a relationship without first checking it does not exist.** No unique constraint exists over edges; uniqueness is a read-before-write, and the same pair holding *different* relations remains valid (LADR-05).
- **Never mirror trackers, repositories or agile hierarchy into the graph.** Those are trees with an authoritative upstream; a copy needs syncing and syncing is a separate product (README, Guiding Principle).
- **All seven LADRs and all four NFRs are Accepted.** Implementations rely on them. Flag a deviation and raise it; do not silently override.
- **Never write an anchor lookup as an inline property map.** `MATCH (n:Memory {memory_uuid: x})` compiles to `properties @> …` and sequentially scans the vertex table; `MATCH (n:Memory) WHERE n.memory_uuid = x` compiles to an extracted-property equality that `ix_memory_vertex_uuid` serves. `MERGE` is the sole exception and has its own GIN index (LADR-06).
- **Never traverse without a bound.** `MemoryPathQuery.MaxDepth` is `required` and capped at 5. Do not add a defaulted overload; a default is a bound the caller never considered (LADR-07).
- **Never return descriptive fields from a read path without the scope plan — and a path's intermediate hops are part of that read.** The endpoint is narrowed by `Plan().RequiredDimension`; every vertex the path crosses is gated by `MemoryScopeFilter.HiddenDimensions`. **Do not drive the hop gate off `Plan().ExcludedDimensions`** — that list is empty for every explicit dimension, so the gate would stop filtering exactly when the caller narrows, and declaring `product` would disclose programme hops that declaring nothing hides. Both the endpoint-only gap and this inversion were found in review and are pinned by tests.

## Architecture Decisions

See [./ladrs/](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| [LADR-01](./ladrs/LADR-01-adopt-age-for-relationships.md) | AGE as an extension in the existing instance | One transaction spans relational and graph work. A separate graph server cannot join that transaction, which is why one was rejected |
| [LADR-02](./ladrs/LADR-02-edges-only-thin-vertices.md) | Vertices carry identity only | Prevents two copies of one truth. Code that reads a memory property from a vertex is wrong by construction |
| [LADR-03](./ladrs/LADR-03-replace-not-dual-write.md) | Replace the table; never dual-write | Any transitional dual-write path is a defect, not caution |
| [LADR-04](./ladrs/LADR-04-connection-session-initialisation.md) | Initialise AGE per physical connection | Start-up-only initialisation prepares one pooled connection and leaves the rest failing intermittently |
| [LADR-05](./ladrs/LADR-05-edge-integrity-as-invariant.md) | Edge integrity is an application invariant | Two guarantees the database used to provide are now the code's job |
| [LADR-06](./ladrs/LADR-06-anchor-lookups-use-property-predicates.md) | Anchor lookups are property predicates, not inline maps | The two Cypher forms compile to different predicates and need different indexes; the inline form was sequentially scanning the vertex table |
| [LADR-07](./ladrs/LADR-07-every-traversal-carries-its-bound.md) | Every traversal carries its bound | `MaxDepth` is `required`, so an unbounded path does not compile |

## Requirements

Bounded multi-hop traversal — the capability Goal 1 exists for. Delivered.

### Traversal contract — delivered

`IMemoryTraversal.FindPathsAsync(MemoryPathQuery)` returns bounded variable-depth `:LINKS` paths
between memory identities, each path carrying its hops in order (source, target, relation, reason)
and the endpoint memory's cheap descriptive fields. Exposed as `POST /api/context/paths` through
`Features/Links/FindPaths`.

- **The bound is `required`, not defaulted.** `MemoryPathQuery.MaxDepth` is a `required` property validated to `1..5`. Nothing in AGE stops a path query walking the whole graph; a forgotten bound must be a compile error, not an incident (LADR-07).
- **Filterable by relation type and direction.** A single relation goes into the variable-length edge's property map so the predicate is applied during expansion. Direction is `Outbound` / `Inbound` / `Either`.
- **Descriptive fields come from the relational rows in the same statement.** The Cypher call is composed with `JOIN memory / memory_version / memory_group` in one SQL statement, never an application-side join — that single-session composition is LADR-01's stated justification, so forfeiting it forfeits the reason for choosing an in-database extension.
- **The scope rule applies.** A traversal returning `description` / `statement` is a read path, so `MemoryScopeFilter.Plan` is pushed into the composed SQL exactly as `MemorySearchCriteria` does. Omitting it reopens the hole LADR-003 (HLD 001 API) was written to close.
- Exposed as `POST /api/context/paths`; the mimisbrunnr-context-memory skill exposes it as the `paths` subcommand (bounded traversal, `maxDepth` required).

### Access path

Measured, not assumed — see LADR-06. AGE compiles the two anchor-lookup forms differently, and each
needs its own index. Created by `20260914120000_AddGraphPropertyIndexes`:

| Cypher | Compiles to | Index |
|---|---|---|
| `MATCH (n:Memory) WHERE n.memory_uuid = …` | `agtype_access_operator(VARIADIC ARRAY[properties, '"memory_uuid"'])` | btree over that expression |
| `MATCH (n:Memory {memory_uuid: …})` | `properties @> '{…}'::agtype` | GIN over `properties` |

`MERGE` cannot be rewritten to the predicate form — its pattern *is* the match — so both indexes
exist. Path expansion itself rides AGE's own `LINKS_start_id_idx` / `LINKS_end_id_idx` and appears in
plans as `Function Scan on age_vle`.

### Evidence, as committed

| Requirement | Evidence | Result |
|---|---|---|
| NFR-02 three shapes at 3,000 memories / 10,000 edges | [nfrs/NFR-02-traversal-measurements.md](./nfrs/NFR-02-traversal-measurements.md) | 1.042 / 0.621 / 14.053 ms p95 against 50 / 10 / 100 ms |
| NFR-02 one-hop vs the pre-cutover baseline | same file, *The one-hop comparison* | 0.621 ms vs 0.429 ms — 1.4×, accepted with the reasoning recorded |
| NFR-03 restore round-trip | [nfrs/NFR-03-restore-verification.md](./nfrs/NFR-03-restore-verification.md) | 201 rows / 200 vertices / **500 edges** matched; 1,797 paths traversed after restore |
| NFR-04 pairing and pre-upgrade check | [nfrs/NFR-04-version-pairing.md](./nfrs/NFR-04-version-pairing.md) | minor round-trip passed; major classified; dev snapshot blocked |

Evidence lives in this folder, not in pull-request comments. The benchmark is env-gated
(`SMOOTH_AGE_BENCH=1`) so the PR gate stays fast; the operational checks are
[`scripts/verify-graph-restore.sh`](../../../scripts/verify-graph-restore.sh) and
[`scripts/verify-graph-preupgrade.sh`](../../../scripts/verify-graph-preupgrade.sh), with
[`scripts/seed-graph-sample.sh`](../../../scripts/seed-graph-sample.sh) to give them something to
verify.

## Key Behaviors

- **Session preparation is per connection, not per database.** A correctly installed extension still fails on a connection that has not loaded it and set its search path. Failures are load-dependent and intermittent, which makes them easy to misdiagnose as flakiness.
- **Graph catalog writes are transactional and must commit to become visible.** Creating a graph or a label inside an uncommitted transaction leaves it invisible to other sessions. Migration tooling wraps statements in a transaction by default, so these statements are deliberately not transaction-wrapped.
- **A scope that looks like a transaction may be a savepoint.** Releasing a savepoint is not a commit; the catalog write stays invisible until the outer transaction commits.
- **Install ordering matters.** The extension must be created before other roles gain schema-creation rights, or its catalog schema can be pre-created under the wrong owner and installation refuses.
- **Traversal returns identities, never content.** Answering what those memories say requires selecting the relational rows — one composed query, since SQL and Cypher share the session. `NpgsqlMemoryTraversal` does exactly that: `ag_catalog.cypher(...)` in a `FROM`, joined to `memory` / `memory_version` / `memory_group` in the same statement. An application-side join would forfeit LADR-01's justification.
- **A disjunction over two vertex instances cannot use an index.** `WHERE s.memory_uuid = x OR t.memory_uuid = x` hash-joined the whole edge table against two full vertex scans — 6.075 ms p95 over `Seq Scan on "LINKS"`, and it still passed the 10 ms target. The one-hop is two anchored matches unioned. `UNION`, not `UNION ALL`: a self-link satisfies both branches and must be reported once.
- **AGE 1.7 cannot cast a path to text and cannot read properties inside a list comprehension.** `RETURN p` fails with `agtype_value_to_text: unsupported argument agtype 8`; `[r IN relationships(p) | r.relation]` fails with `could not find properties for r`. Return `nodes(p)` and `relationships(p)` and parse them. `UNWIND relationships(p) AS r` *does* resolve `r.relation`, but flattens per-path grouping.
- **`nodes()` / `relationships()` come back with `::vertex` / `::edge` annotations that no JSON parser accepts.** `AgtypeArrayReader` strips them by scanning and skipping string literals, not by text replacement — `reason` is caller-supplied free text and may legitimately contain `}::edge`, which an L1 theory pins.
- **Hop orientation comes from the edge, not from the walk.** An `Either`-direction traversal crosses edges backwards, so a hop's source and target are resolved from `start_id` / `end_id`, never from the order `nodes(p)` returned. Direction is part of a relationship's identity.
- **The intermediate-hop gate roughly doubles the composed shape, 8.4 → 14.1 ms, and that is the price of not leaking.** Two SQL formulations measured the same, so the simpler join is kept. The `cardinality(...) = 0` short-circuit is load-bearing for correctness measurement as well as speed: an earlier benchmark left the excluded list empty, tripped the short-circuit, and reported a gate cost of 0.15 ms for a gate that never ran. A benchmark whose query is not the query the API issues measures nothing.
- **The node list is safe to convert with text replacement; the edge list is not.** A vertex carries `memory_uuid` only (LADR-02), so no caller-supplied string reaches its rendering and `replace(…, '::vertex', '')` cannot corrupt it. An edge carries `reason`, so it goes through `AgtypeArrayReader`'s scanner instead. The asymmetry is a consequence of edges-only, not an inconsistency.
- **The open-ended traversal scans the vertex table, deliberately.** Asking for everything reachable has no second endpoint to index against; the depth bound is what limits it, not an index (LADR-07). Edge storage is still reached only through `age_vle`, which is what NFR-02's criterion names.
- **The entity-count guard changes by one.** Removing the relationship entity is expected and is updated deliberately in the same change; it is not a test to weaken when it fails.
- **Relationships live only in the graph.** Vertex label `Memory` (`memory_uuid` only); one edge label `LINKS` with properties `relation` (open vocabulary) and `reason` (mandatory). The five foundation elabels were dropped at cutover. `memory_link` is gone.
- **The delete path is the `trg_memory_graph_cascade` trigger, and nothing else.** Removing a memory row fires a BEFORE DELETE trigger that DETACH DELETEs its vertex in the same statement and transaction. `IMemoryGraph` deliberately exposes no delete method; a C# edge-removal step beside the trigger is a second delete path (Quality Constraints), not belt-and-braces.
- **The relationship contract is characterised** in `LinkTests` (HLD-003): a duplicate directed triple is refused, the same pair may hold several relations, direction is identity, deleting a memory removes inbound and outbound edges, a self-link persists at the store, and links are not group-bounded.
- **Setting the AGE search path changes `current_schema()`, and EF resolves the migrations-history table against it.** `search_path = ag_catalog, "$user", public` makes `current_schema()` return `ag_catalog`, so an unqualified `__EFMigrationsHistory` is looked for there, not found, and EF concludes the database has never been migrated — then re-applies the first migration and fails on objects that already exist. The first start of a fresh database succeeds because the extension is not installed yet when history is first read; every start afterwards fails. The history table is therefore schema-pinned (`MigrationsHistoryConvention`). Anything else that resolves an unqualified object name at runtime is exposed to the same shift and must qualify it.
- **Do not revert the database image to `library/postgres`.** Both Aspire hosts must stay on `docker.io/apache/age:release_PG17_1.7.0`. A vanilla Postgres image fails `CREATE EXTENSION age` and the pool-recycle tests.

## Quality Constraints

Measurable targets and their verification live in [./nfrs/](./nfrs/) — integrity, performance,
operability and compatibility. Two shape how code is written rather than merely how it is measured:

- The delete path must have exactly **one** implementation. The orphan invariant is only as strong as its least careful caller, so a second delete path is a defect.
- Relationship access is written against the database driver directly. The ORM does not model graph objects, so they are absent from its migration model and its snapshot does not describe them.

## Migration Plans

- The relational relationship table is dropped; existing rows are carried over as `:LINKS` edges in the same migration (LADR-03).
- Reversal is a corrective migration restoring the table — there is no fallback flag, by design.
- The database image is `docker.io/apache/age:release_PG17_1.7.0` (Postgres 17 + AGE 1.7.0) in both the development and test hosts. Pairing: [nfrs/NFR-04-version-pairing.md](./nfrs/NFR-04-version-pairing.md).

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-14 | Review fix: the access note claimed the skill has no subcommand for path traversal — the `paths` subcommand has existed since 2026-09-13 (bounded traversal, `maxDepth` required). | HLD-003 |
| 2026-09-14 | Agent-facing skill renamed `context-memory` → `mimisbrunnr-context-memory`. | skill rename |
| 2026-09-13 | Review fix: the evidence summaries quoted the superseded pre-gate run; every document now quotes the Results-table run (1.042 / 0.621 / 14.053 ms p95, one-hop delta +0.192 ms), and the historical tables are marked as such. | NFR-02 |
| 2026-09-13 | Review fix: the hop gate read `Plan().ExcludedDimensions`, which is empty for every explicit dimension, so declaring `product` disclosed programme intermediates that declaring nothing hid — the inverse of the blob proxy's consent model. Hop visibility now comes from `MemoryScopeFilter.HiddenDimensions`. | HLD-003 |
| 2026-09-13 | Corrected the recorded gate cost: the benchmark had left the excluded list empty and tripped the short-circuit, so the reported 0.15 ms was a gate that never ran. Gating hops costs ~5.7 ms (8.4 → 14.1 ms); the membership rewrite bought nothing and was reverted. | NFR-02 |
| 2026-09-13 | Review fix: the scope rule gated only a path's endpoint, so a route through a programme-scoped memory disclosed its identity and its edges' reasons. Intermediates are now gated in the same statement. First shape doubled the composed query to 16.1 ms; reshaped as a membership test it costs 0.15 ms. | HLD-003 |
| 2026-09-13 | Review fix: the one-hop baseline comparison is now asserted rather than only reported — the benchmark fails above 4× the baseline (widened from 3× after a busy-machine repeat run came within noise of the 3× ceiling), so a widening regression fails the build instead of producing a green test. | NFR-02 |
| 2026-09-13 | Bounded multi-hop traversal shipped: `IMemoryTraversal` + `NpgsqlMemoryTraversal` (composed Cypher-and-SQL statement), `Features/Links/FindPaths`, `POST /api/context/paths`. Depth bound `required` and capped at 5. Scope plan applied to the new read path. | HLD-003, LADR-07 |
| 2026-09-13 | Every anchor lookup was sequentially scanning the vertex table — the inline property map compiles to `properties @>`, which no index served. Added `ix_memory_vertex_uuid` (btree), `ix_memory_vertex_properties` (GIN, for `MERGE`) and `ix_memory_links_relation`; rewrote `ExistsAsync` to the predicate form. | LADR-06 |
| 2026-09-13 | One-hop rewritten from `OR` to two anchored matches unioned. Measured 6.075 → 0.616 ms p95; plan cost 506.81 → 35.71. The `OR` form passed the 10 ms target while scanning edge storage — the plan requirement is what caught it. | NFR-02 |
| 2026-09-13 | NFR-02, NFR-03, NFR-04 Accepted against committed evidence; LADR-06 and LADR-07 added and Accepted; HLD status Accepted. One-hop at 1.4× the relational baseline adjudicated as accepted, with the reasoning and the reopening conditions on the record. | HLD-003 |
| 2026-09-13 | LADRs 01–05 and NFR-01 Accepted — cutover + `LinkTests` + pool-recycle shipped. NFR-02/03/04 stay Draft until measured. | HLD-003 |
| 2026-09-13 | Cutover: `memory_link` replaced by `:LINKS` edges; identity-only vertices; delete trigger; uniqueness read-before-write. Open relation vocabulary. | HLD-003 |
| 2026-09-13 | Recorded that the AGE search path shifts `current_schema()` to `ag_catalog`, which broke EF's migrations-history lookup and made every restart after the first re-apply migrations. History table pinned to `public`. | HLD-003 |
| 2026-09-13 | Characterised the relational uniqueness/integrity contract in `LinkTests` before cutover. No production change. | HLD-003 |
| 2026-09-13 | Foundation review: both Aspire hosts stay on `docker.io/apache/age:release_PG17_1.7.0`; do not revert to `library/postgres`. | HLD-003 |
| 2026-09-13 | AGE foundation shipped: image pin, per-connection init, empty graph + labels, relational one-hop baseline. `memory_link` untouched. | HLD-003 |
| 2026-09-13 | Added the upstream BRD as cited business authority; BR-11 requires the connection's reason, not only the edge. | BRD 001 |
| 2026-09-13 | Created — edges-only graph adoption, five LADRs, four NFRs, C1 + ER + sequence diagrams. | Amends HLD 001 |
