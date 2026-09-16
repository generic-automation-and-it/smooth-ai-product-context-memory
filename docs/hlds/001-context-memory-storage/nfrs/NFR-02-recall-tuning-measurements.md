# NFR-02 evidence: recall-tuning measurements (text-search configuration)

**Date:** 2026-09-16
**Status:** Measured — selected `english`; `pg_trgm` rejected; both deferrals closed
**Command:** `SMOOTH_FTS_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest --filter RecallTuningEvidenceTests`
**Environment:** Aspire test fixture, `docker.io/apache/age:release_PG17_1.7.0`, isolated per-test database, macOS/arm64 dev host.

## Question

HLD-001 deferred two candidate-recall improvements — stemming and trigram similarity — "until
measurement justifies them". This records the measurement and the decision.

## Method

One committed synthetic fixture (`RecallTuningEvidenceTests`, 14 memories) containing inflection
pairs, misspelled surface variants and related-but-distinct negative controls, queried with 11 cases
across three predicates over the exact production shape (subject row OR current version row):

- `simple` — the previous configuration (exact lexeme match, no stemming);
- `english` — stemmed PostgreSQL configuration, candidate indexes created test-locally;
- `pg_trgm` — word-similarity (`%>`, default threshold) over the same concatenated expressions.

Precision/recall are counted per case and micro-aggregated; plans captured with
`SET LOCAL enable_seqscan = off`; p50/p95 over 100 iterations of the representative query.

## Results

| Config | Micro precision | Micro recall | p50 (ms) | p95 (ms) |
|---|---|---|---|---|
| `simple` (previous) | 1.00 | 0.44 | 0.390 | 0.497 |
| `english` (selected) | 0.88 | 0.78 | 0.416 | 0.480 |
| `pg_trgm` (rejected) | 1.00 | 0.78 | 0.426 | 0.515 |

Per-case highlights:

- `simple` missed every inflected query ("migration" vs stored "migrations", "cached responses" vs
  "caching") — the deferred failure class, confirmed real.
- `english` recovered all four inflection cases. Its single false positive is a stem collision:
  "authorization rules" → "Book authors registry" (both stem to `author`). One extra candidate row,
  bounded by the query limit, adjudicated by the skill's semantic judgement.
- `pg_trgm` matched the inflection cases and the "postgress" misspelling but missed "kubernets", and
  its behaviour hangs on a similarity threshold. Equal recall to `english` at the cost of an
  extension, two additional GIN indexes and threshold-sensitive semantics; small-fixture precision
  does not predict its precision at real vocabulary scale.

## Plans

All three predicates produce the identical join shape: the OR across the subject and version tables is
a join filter, so the per-table FTS GIN indexes cannot serve the combined OR directly — for any
configuration, including the previous one. This is pre-existing and config-independent, not a
regression of the selected option. The single-table branch is index-served; the always-run
`FreeTextRecallTests` asserts `ix_memory_name_description_fts` serves it with no sequential scan.

## Decision

- **Selected: `english`** — recall 0.44 → 0.78 on the deferred failure class; the measured precision
  cost is one pinned stem-collision candidate (`FreeTextRecallTests.Known_stem_collision_is_a_pinned_candidate_widening`).
  Shipped as the `StemFullTextIndexes` migration (index side) plus the matching
  `NpgsqlMemorySearch.TextSearchConfig` change (query side) — the two must always name the same
  configuration.
- **Rejected: `pg_trgm`** — no recall advantage over `english` on this fixture; adds an extension,
  index storage and threshold sensitivity. **Reopening threshold:** HLD-004 recall-feedback data (once
  implemented) showing repeated misses on misspelled or surface-variant queries that stemming cannot
  serve; re-run this harness with those observed cases added to the fixture before adopting.

Semantic equivalence judgement remains in the `mimisbrunnr-context-memory` skill; the server-side
change widens candidate recall only and preserves every scope, status, temporal, facet and limit
semantic (pinned by `FreeTextRecallTests` and the existing query component tests).
