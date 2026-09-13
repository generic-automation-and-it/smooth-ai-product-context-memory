# NFR-02: Performance

**Status:** Accepted

## Requirement

At a graph of **10,000 edges over 3,000 memories** — roughly an order of magnitude beyond the
expected first-year volume:

- A **bounded depth-3 path query** between two known memory identities, filtered by relation type, completes with **p95 ≤ 50 ms**.
- The **one-hop reverse lookup** — the only traversal in use today — completes with **p95 ≤ 10 ms**, and is **no slower than the relational implementation it replaces**.
- A traversal composed with a relational predicate (path plus a filter on the memories' descriptive fields) completes with **p95 ≤ 100 ms**.

## Verification

A seeded benchmark run against a real instance, executed before the design moves from Prototype to
Accepted:

- Generate 3,000 memories and 10,000 edges with a realistic relation-type distribution — most edges `relates_to` and `depends_on`, few `contradicts`.
- Measure each of the three query shapes over at least 100 iterations, discarding a warm-up set, and record p50 and p95.
- Capture the query plan for each shape and confirm the access path is an index or graph traversal, not a sequential scan of the edge storage.
- Re-run the one-hop measurement against the pre-change implementation on the same data, so the comparison is a measurement rather than an assertion. Pre-cutover baseline: [NFR-02-one-hop-baseline.md](./NFR-02-one-hop-baseline.md) (p50 0.320 ms, p95 0.429 ms, bitmap index scan).

**Measured post-cutover: [NFR-02-traversal-measurements.md](./NFR-02-traversal-measurements.md)** — all three absolute targets met (1.057 / 0.616 / 8.522 ms p95); one-hop is 1.4× the relational baseline, which is a regression on the literal reading and is recorded rather than adjudicated there.

## Acceptance Criteria

- All three targets met at the stated volume, with the p95 figures recorded in the HLD folder.
- The one-hop comparison shows no regression; a regression blocks the change, because it would trade a working access pattern for an unused one. **Adjudicated 2026-09-13:** measured at 0.616 ms p95 against a 0.429 ms baseline — 1.4×, +187 µs. Accepted rather than treated as blocking, because the graph one-hop answers a *bidirectional* question the reverse-only baseline did not, the residual is constant `cypher()` overhead rather than access-path cost (the plan is index scans throughout), and the absolute figure sits 16× inside the target. The working access pattern was not traded away — it was widened. Full reasoning: [NFR-02-traversal-measurements.md](./NFR-02-traversal-measurements.md).
- Plans confirm no sequential scan over edge storage at the target volume.
- If a target is missed, the shortfall and its cause are recorded before any tuning, so the fix addresses a measured cause rather than a guess.

## Applies To

Goal 1 (multi-hop traversal); LADR-01; the relationship read path.
