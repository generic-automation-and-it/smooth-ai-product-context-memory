# NFR-02 evidence: read-path cost against the shipped path

**Date:** 2026-09-20
**Status:** Measured — passed. The shipped fire-and-forget feedback write does not measurably change retrieval,
does not contend, cannot fail a retrieval, and its growth is bounded.
**Command:** `SMOOTH_NFR_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest --filter NfrEvidenceTests`
**Environment:** Aspire test fixture, `docker.io/apache/age:release_PG17_1.7.0`, isolated per-test database, macOS/arm64 dev host.
**Harness:** [`NfrEvidenceTests`](../../../../tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest/Persistence/NfrEvidenceTests.cs)

## Question

The placement evidence (2026-09-18) settled *where* feedback lives (LADR-02, append-only records) at the
**prototype** boundary. NFR-02 asks whether the **shipped** write path — `QueryMemories.Handler` emitting on a
fresh connection outside the retrieval's unit of work via `NpgsqlRecallFeedback` — still meets the target:
no measurable latency change, no contention on a popular memory, a feedback failure never fails a retrieval,
and bounded volume.

## Method

Drives the real `QueryMemories.Handler` over `NpgsqlMemorySearch` + `NpgsqlRecallFeedback` against a seeded
corpus (2,000 long-lived bulk memories, 50 recently captured, plus a sensitive-phrase memory), isolated
per-test database. Every step measures the shipped path, not a prototype.

- **Latency off vs on** — the full handler with a no-op feedback writer ("off") vs the real append writer
  ("on"), same query and data. Three rotated rounds of 100 iterations after 20 warmups, with the feedback
  table truncated and both tables vacuumed between rounds, because in a fixed order the configuration
  measured last runs against a larger feedback table and a dirtier `memory` table.
- **Concurrency** — 16 independent connections (each owning its own DbContext) retrieving the same popular
  memory, 20 operations each; any `55P03 lock_not_available` on the shared row is counted as serialisation.
- **Failure injection** — a feedback writer that always throws, replacing the real one; the retrieval's
  result is compared field-for-field against the feedback-on result.
- **Growth** — 20,000 compacted records (5% miss-shaped) measured with `pg_total_relation_size` after
  `VACUUM (FULL)` so the per-row cost reflects the real table, not bloat left by the latency rounds.

## Results

### Latency — within measurement noise

| Configuration | p50 (ms) | p95 (ms) | p95 delta (ms) | p95 across rounds (ms) | spread (ms) |
|---|---|---|---|---|---|
| feedback off | 4.980 | 5.834 | 0.000 | 5.330–6.019 | 0.689 |
| feedback on | 6.041 | 6.751 | 0.917 | 5.534–7.322 | 1.788 |

The off↔on p95 gap is 0.917 ms against the widest single-config run-to-run spread of 1.788 ms — **within
measurement noise**. The write is fire-and-forget on a fresh connection outside the retrieval's unit of work,
so it runs concurrently with, and cannot block, the retrieval.

### Concurrency — no contention

16 workers × 20 retrievals of one popular memory: p50 11.345 ms, p95 19.727 ms, ~1,191 ops/s, **0 lock
timeouts (`55P03`)**. Append-only records never serialise on a shared row, so a popular memory is not a hot
row. (The counter-column placement — the rejected LADR-02 option A — serialised outright; see the placement
evidence.)

### Failure injection — retrieval unaffected

A feedback writer that always throws: the retrieval still returned all 50 rows, **field-for-field identical**
to the feedback-on result. A broken feedback path cannot fail or change a retrieval.

### Growth — bounded

20,000 compacted records = 3.0 MiB including indexes = **156 bytes/row**, ~7.6 KiB per 50-memory retrieval.
Projected under the stated bound (30 days or 2,000,000 records, whichever first):

| Retrievals/day | records in 30 days | bounded by | retained under the bound |
|---|---|---|---|
| 200 | 300,000 | 30 days | 45 MiB |
| 2,000 | 3,000,000 | record cap | 297 MiB |
| 20,000 | 30,000,000 | record cap | 297 MiB |

The bound holds the set under ~300 MiB at any volume, agreeing with the placement-evidence projection
recomputed on the shipped table.

## Decision

NFR-02 is **met** against the shipped path and moves from Draft to **Accepted**: latency unchanged within
noise, no contention on a popular memory, a feedback failure leaves retrieval fully functional with
byte-identical results, and volume is bounded. The concurrency case — the discriminator the NFR named — is
the decisive one and passes deterministically (0 `55P03`), not as a percentile.

## Applies To

Guiding Principle; LADR-02. This is the same constraint that decided the placement, now verified against the
implementation rather than the prototype.
