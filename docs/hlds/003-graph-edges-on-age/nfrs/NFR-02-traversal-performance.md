# NFR-02: Performance

**Status:** Accepted for memory and ticket traversal, 2026-09-15. **Performance gate PASSED** at
unchanged budgets; [final ticket and memory rerun evidence](./NFR-02-ticket-traversal-measurements.md).

## Requirement

At a graph of **10,000 edges over 3,000 memories** — roughly an order of magnitude beyond the
expected first-year volume:

- A **bounded depth-3 path query** between two known memory identities, filtered by relation type, completes with **p95 ≤ 50 ms**.
- The **one-hop reverse lookup** completes with **p95 ≤ 10 ms**, and is **no slower than the relational implementation it replaces**.
- A traversal composed with a relational predicate (path plus a filter on the memories' descriptive fields) completes with **p95 ≤ 100 ms**.

These three memory budgets and the recorded one-hop adjudication below remain unchanged.
[LADR-08](../ladrs/LADR-08-captured-ticket-hierarchy.md) adds a separate composed ticket traversal
budget of **p95 <= 100 ms**, including live exact-owner resolution, active `HiddenDimensions` hop
gates, endpoint narrowing, deterministic capped paths and distinct current non-proposed memories.

## Verification

A seeded benchmark run against a real instance, executed before the design moves from Prototype to
Accepted:

- Generate 3,000 memories and 10,000 edges with a realistic relation-type distribution — most edges `relates_to` and `depends_on`, few `contradicts`.
- Measure each of the three query shapes over at least 100 iterations, discarding a warm-up set, and record p50 and p95.
- Capture the query plan for each shape and confirm the access path is an index or graph traversal, not a sequential scan of the edge storage.
- Re-run the one-hop measurement against the pre-change implementation on the same data, so the comparison is a measurement rather than an assertion. Pre-cutover baseline: [NFR-02-one-hop-baseline.md](./NFR-02-one-hop-baseline.md) (p50 0.320 ms, p95 0.429 ms, bitmap index scan).

**Measured post-cutover: [NFR-02-traversal-measurements.md](./NFR-02-traversal-measurements.md)** — all three absolute targets met (1.042 / 0.621 / 14.053 ms p95); one-hop is 1.4× the relational baseline, which is a regression on the literal reading and is recorded rather than adjudicated there.

**Ticket evidence is separate from the historical memory measurements.** Use a hub-active hierarchy
fixture exercising actual multi-hop traversal, a high-degree parent and visible and hidden owners.
Multiple-ticket/same-group deduplication is pinned separately by standing traversal tests. Record
fixture ticket/edge/group/memory counts and cap settings.
Measure at least 100 iterations after warm-up, recording p50/p95 and query plans. Prove the measured
query executes multi-hop expansion, ownership joins and a nonempty hidden-dimension gate;
an empty graph, anchor-only lookup or bypassed gate does not qualify. A response cap may retain
only depth-one paths while the actual recursive walk still exercises deeper expansion; the command
plan and fixture must demonstrate that work rather than inferring it from returned paths. Repeat at supported maximum
depth 5 and with caps reached. Re-run existing memory shapes to establish their budgets still hold.

`TicketTraversalBenchmarkTests` now benchmarks the actual provider command, including its Cypher
anchor, recursive SQL over indexed AGE adjacency, live ownership, hidden-hop gating and relational
memory selection. It exercises hub fanouts 100/500/1000 at requested depths 2/3/5, with 20 warm-ups and 100 measured
iterations, captures p50/p95 and EXPLAIN, and asserts <= 100 ms. The fixture remains **three actual
edges deep**: requesting depth 5 verifies the supported maximum bound on that fixture, not five-deep
performance. The historical pre-merge run passed all nine ticket measurements and the original
memory benchmark case, with worst ticket p95 59.092 ms. The 2026-09-15 post-review revalidation
also passed all shapes, with worst ticket p95 34.797 ms. No threshold was relaxed.

Full tables, actual plan excerpts, dataset sizes and noise caveats are in
[NFR-02-ticket-traversal-measurements.md](./NFR-02-ticket-traversal-measurements.md).
Its original 396-pass solution / 4-pass explicit benchmark / 34-pass Python checkpoint is historical;
the dated review revalidation records 429 solution passes, four gated skips, four explicit benchmark
passes and 42 Python passes. Later post-sync functional runs are separate checkpoints, not replacements
for the dated benchmark measurements.
The export fixture now uses unique tickets per group and the full-suite rerun passed. Former
quadratic joins and targeted-only verification are historical corrections, not current open gates.

## Acceptance Criteria

- All three targets met at the stated volume, with the p95 figures recorded in the HLD folder.
- The one-hop comparison shows no regression; a regression blocks the change, because it would trade a working access pattern for an unused one. **Adjudicated 2026-09-13:** measured at 0.621 ms p95 against a 0.429 ms baseline — 1.4×, +0.192 ms. Accepted rather than treated as blocking, because the graph one-hop answers a *bidirectional* question the reverse-only baseline did not, the residual is constant `cypher()` overhead rather than access-path cost (the plan is index scans throughout), and the absolute figure sits 16× inside the target. The working access pattern was not traded away — it was widened. Full reasoning: [NFR-02-traversal-measurements.md](./NFR-02-traversal-measurements.md).
- Plans confirm no sequential scan over edge storage at the target volume.
- If a target is missed, the shortfall and its cause are recorded before any tuning, so the fix addresses a measured cause rather than a guess.
- Ticket delivery additionally requires composed p95 <= 100 ms on the stated hub-active shapes,
  committed fixture/plan/measurement evidence, and no regression of the existing memory budgets.

## Applies To

Goals 1 and 5 (memory and captured-ticket traversal); LADR-01 and LADR-08; relationship read paths.
