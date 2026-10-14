# NFR-02: Index-served performance

**Status:** Draft

## Requirement

The single-table FTS branch for each `tsquery` constructor (`plainto`, `to_tsquery`,
phrase, prefix) is GIN-served (`ix_memory_name_description_fts` and
`ix_memory_version_statement_summary_fts`) with no sequential scan on branch `EXPLAIN`. The full production shape
(subject-row OR current-version-row join filter) is documented with a captured plan at
Prototype — HLD-001 records that the combined OR cannot use the per-table GIN directly,
so no index-served assertion is made on the combined shape. Added rank+headline cost is
bounded: p95 with rank+headline ≤ 2x the re-measured bare-FTS p95 on the declared corpus
(see Verification). `ts_headline` executes post-`LIMIT` only.

## Verification

`EXPLAIN (FORMAT TEXT)` in an explicit transaction (`BEGIN … ROLLBACK`, required: `SET
LOCAL enable_seqscan = off` reverts on rollback) per `tsquery` constructor
on the single-table branches (extends `FreeTextRecallTests`; forcing is justified
because it distinguishes index-availability from planner choice at fixture scale, and
the unforced plan is captured alongside informationally); full-shape `EXPLAIN`
captured informationally at Prototype with no index assertion on the combined OR.
Latency harness (`KeywordPerfTests`, `SMOOTH_FTS_BENCH=1` pattern, 100 iterations of the
representative operator query `migration OR rollback -proposed "schema migrations"`
committed in the fixture) reports p50/p95 plus absolute ms on a declared corpus, concurrency, and host (Aspire
fixture, `docker.io/apache/age:release_PG17_1.7.0`, arch — same fields as the HLD-001
method). The HLD-001 0.48 ms figure is stale (post-2026-09-24 predicate change) and is
not the baseline; the baseline is re-measured at Prototype. Rank/headline cost measured
snippet-non-null vs. snippet-null.

## Acceptance Criteria

- [ ] Branch plan test green for all `tsquery` constructors on single-table branches;
  branch `EXPLAIN` names the FTS GIN index with no `Seq Scan`.
- [ ] Full-shape plan captured and attached; combined-OR limitation stated, not hidden.
- [ ] p95 with rank+headline ≤ 2x re-measured bare-FTS p95 on the same declared corpus,
  concurrency, and host.
- [ ] Composed keyword+traverse timing reported separately against the HLD-003
  methodology; the 100 ms ticket-traversal budget is not reused as this shape's target
  without a re-measurement clause.

## Applies To

Goals 1, 3, 4; LADR-02, LADR-04, LADR-05.
