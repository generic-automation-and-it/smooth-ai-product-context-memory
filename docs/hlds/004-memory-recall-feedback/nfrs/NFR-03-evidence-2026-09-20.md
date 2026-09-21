# NFR-03 evidence: actionability against the shipped query surfaces

**Date:** 2026-09-20
**Status:** Measured — passed. The three tuning questions are answerable from the committed queries.
**Command:** `SMOOTH_NFR_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest --filter NfrEvidenceTests`
**Environment:** Aspire test fixture, `docker.io/apache/age:release_PG17_1.7.0`, isolated per-test database, macOS/arm64 dev host.
**Harness:** [`NfrEvidenceTests`](../../../../tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest/Persistence/NfrEvidenceTests.cs)

## Question

NFR-03 is the test of whether the design earns its cost: the feedback surface must answer its three named
questions from the committed queries, not by ad-hoc inspection of raw records. The placement evidence
verified this at the prototype boundary; this verifies the **shipped** `NpgsqlRecallFeedbackQuery`.

## Method

Seeded a corpus with a long-lived pool (created 30 days ago) and a recently-captured cohort (created now),
then drove the real `QueryMemories.Handler` + `NpgsqlRecallFeedbackQuery` against it:

1. **Never recalled** — recalled a subset of the long-lived pool via the real handler (recording exactly which
   uuids it returned), then queried never-recalled and compared it against the ground-truth untouched set
   (every long-lived memory minus the recalled subset), computed directly from the store. Queried with a large
   limit so the full pool returns (the query surfaces a 500-row default that would make the equality vacuous).
2. **Recently-captured exclusion** — asserted none of the never-recalled list is recently captured, and that
   all 50 recently-captured are correctly excluded by the 7-day grace clause.
3. **Miss rate** — performed a known 7-miss / 1-hit mix over the window; asserted the miss-rate query recovers
   the retrievals and misses.
4. **Before/after** — reset to a clean baseline, then re-measured the identical window, so a later tuning
   change is measured against a reset baseline rather than accumulated noise (LADR-04).

## Results

- **Never-recalled equality:** the never-recalled list matched the untouched set **exactly** — all 1,904
  members of the long-lived untouched pool, and none recalled.
- **Recently-captured exclusion:** the 7-day grace clause correctly excluded all 50 recently-captured; none
  leaked into the never-recalled list.
- **Miss rate:** 9 retrievals in the window, 7 of which returned nothing (77.8%), matching the seeded
  7-miss / 1-hit mix. Misses are countable over a period, so a tuning change can be shown to move it.
- **Reset / before-after:** resets to a clean baseline and re-measures the identical window, so a tuning
  change is attributable rather than confounded by prior records.

## Decision

NFR-03 is **met** and moves from Draft to **Accepted**: all three tuning questions are answerable from the
committed queries (`GetNeverRecalledMemories`, `GetMissRate`, `ResetRecallFeedback`), the never-recalled list
distinguishes never-recalled from recently-captured, and the baseline is resettable for a meaningful
before/after comparison.

## Reopening note

This harness, like the placement and recall-tuning evidence, is env-gated (`SMOOTH_NFR_BENCH=1`) so it is
recorded evidence, not a PR-gate assertion. Re-run it (and update this doc) if the retrieval limit rises
materially, if feedback is ever proposed as an input to ranking, or if the never-recalled query is
re-expressed. See also the placement evidence's reopening threshold.

## Applies To

All three Key Goals. This NFR is whether the design earns its cost — and it does.
