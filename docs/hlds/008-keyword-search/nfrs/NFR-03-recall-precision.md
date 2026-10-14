# NFR-03: Recall-precision quality

**Status:** Draft

## Requirement

On the fixed 20-case extended fixture (HLD-001's 11 cases plus 9 new operator cases, no
additions without re-baselining: 2 phrase-narrows, 2 OR-widens, 2 NOT-excludes, 2
prefix-completes, 1 field-narrows), micro-recall ≥ 0.78 (no regression vs. the shipped
`english` decision) and micro-precision ≥ 0.85, micro-averaged per case over the 20.
Both numbers are Prototype targets, not citations — the HLD-001 figures carry a
stale-before-quoting banner (post-2026-09-24 predicate change) and are re-measured with
the new cases before acceptance. The known stem collision (`author`) stays pinned; new
operator widening (OR/prefix) may add documented candidates beyond that single pin,
each named in the evidence.

## Verification

`KeywordRecallBench` (`SMOOTH_FTS_BENCH=1`-pattern) over the exact production shape
(subject row OR current version row), per-case precision/recall micro-aggregated over
the 20 exactly as HLD-001 NFR-02. Rank assertion runs on 3 dedicated multi-lexeme rows
in the same corpus. Evidence artifact:
`docs/hlds/008-keyword-search/nfrs/NFR-03-evidence.md` (Prototype deliverable: per-case
table + aggregates), attached at Prototype.

## Acceptance Criteria

- [ ] Micro-recall ≥ 0.78 and micro-precision ≥ 0.85 on the extended fixture,
  re-measured at Prototype (HLD-001 numbers not quoted as evidence).
- [ ] Rank fixture scoped to operator queries: multi-lexeme exact row orders first
  regardless of `ValidFrom`; set equality with the unranked query holds; bare order
  covered by NFR-04 instead.

## Applies To

Goals 1–3; LADR-02, LADR-04, LADR-06.
