# HLD-001 `pg_trgm` reopening threshold — evidence

**Date:** 2026-09-20
**Status:** Observed — **reopening threshold MET** (the adopting decision remains HLD-001's, not this doc's).
**Command:** `SMOOTH_NFR_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest --filter NfrEvidenceTests`
**Environment:** Aspire test fixture, `docker.io/apache/age:release_PG17_1.7.0`, isolated per-test database, macOS/arm64 dev host.
**Harness:** [`NfrEvidenceTests`](../../../../tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest/Persistence/NfrEvidenceTests.cs)

## Question

HLD-001 deferred `pg_trgm` (stemming was adopted, trigram rejected) with a reopening threshold: "repeated
misses on misspelled or surface-variant queries that stemming cannot serve." HLD-004 supplies that data as
observed misses from the recall-feedback signal's retrieval path. This records whether those misses exist —
it does **not** adopt `pg_trgm` (that is HLD-001's gated decision).

## Method

Seeded memories with correct surface forms (Postgres replication, Kubernetes resource quota, plus a
same-stem negative control), then ran misspelled / surface-variant queries through the **shipped**
`QueryMemories.Handler` (which uses the `english` stemming configuration selected in HLD-001) and recorded
whether the target was returned.

## Results

| Query | Target | Labelled | Returned | Type |
|---|---|---|---|---|
| postgress replication | postgres-replication | misspelling of 'postgres' | 0 | miss |
| kubernets quota | kubernetes-quota | misspelling of 'kubernetes' | 1 | hit |
| kubernetess | kubernetes-quota | extra-letter misspelling | 0 | miss |
| cache invalidation | cachet-display | same-stem false positive control | 0 | no false positive |

Observed misses on surface variants (excluding the false-positive control): **2** — "postgress replication"
and "kubernetess", both of which stemming cannot serve.

## Decision

The HLD-001 `pg_trgm` reopening threshold evidence is **MET**: observed misses on misspelled / surface-variant
queries that stemming cannot serve exist. Whether to adopt `pg_trgm` is HLD-001's gated decision; the directed
next step is to re-run the HLD-001 recall-tuning harness with these observed cases added to the fixture
before adopting.

## Applies To

HLD-001 NFR-02 (read-only). This supplies data for a separate HLD's decision; it implements no recall change
here.
