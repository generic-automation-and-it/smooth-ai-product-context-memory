# NFR-03: Recall-precision quality

**Status:** Draft

## Requirement

On the extended fixture (HLD-001's 14 memories / 11 cases plus ≥ 9 new operator cases:
phrase-narrows, OR-widens, NOT-excludes, prefix-completes, field-narrows), micro-recall
≥ 0.78 (no regression vs. the shipped `english` decision) and micro-precision ≥ 0.85.
Both numbers are Prototype targets, not citations — the HLD-001 figures carry a
stale-before-quoting banner (post-2026-09-24 predicate change) and are re-measured with
the new cases before acceptance. The known stem collision (`author`) stays pinned; new
operator widening (OR/prefix) may add documented candidates beyond that single pin,
each named in the evidence.

## Verification

`SMOOTH_FTS_BENCH=1`-pattern harness over the exact production shape (subject row OR
current version row), per-case precision/recall micro-aggregated exactly as HLD-001 NFR-02.
New operator cases recorded with the same per-case highlight format. Re-run before quoting
numbers (HLD-001 figures are stale relative to the 2026-09-24 predicate change).

## Acceptance Criteria

- [ ] Micro-recall ≥ 0.78 and micro-precision ≥ 0.85 on the extended fixture,
  re-measured at Prototype (HLD-001 numbers not quoted as evidence).
- [ ] Rank fixture scoped to operator queries: multi-lexeme exact row orders first
  regardless of `ValidFrom`; set equality with the unranked query holds; bare order
  covered by NFR-04 instead.

## Applies To

Goals 1–3; LADR-02, LADR-04, LADR-06.
