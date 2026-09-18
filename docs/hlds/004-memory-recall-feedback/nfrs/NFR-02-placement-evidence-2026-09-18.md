# NFR-02 evidence: feedback placement measurements

**Date:** 2026-09-18
**Status:** Measured — selected append-only records (LADR-02 option B); counter column and telemetry-derived rejected
**Command:** `SMOOTH_FEEDBACK_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest --filter FeedbackPlacementEvidenceTests`
**Environment:** Aspire test fixture, `docker.io/apache/age:release_PG17_1.7.0`, isolated per-test database, macOS/arm64 dev host.

## Question

LADR-02 deliberately deferred where recall feedback lives, naming three candidates and warning that "a
counter column is the easiest to write and the most likely to be wrong". NFR-02 states that the
concurrency case is the discriminator. This records the measurement and the decision.

## Method

Both write mechanisms are prototyped inside the test's isolated database only; the production schema is
untouched, as `RecallTuningEvidenceTests` does for its candidate indexes. The unchanged read path is the
telemetry-derived baseline, since option C performs no store write at all.

- **A — counter column:** `recall_count bigint` on `memory`, incremented per returned memory in one
  batched `UPDATE … WHERE uuid = ANY($1)`.
- **B — append-only records:** a separate table of `(retrieval_id, memory_uuid, shape, occurred_on)` with
  identity, recency and retrieval indexes, and a `CHECK` on `shape` standing in for the bounded
  classification set. One batched multi-row `INSERT` per retrieval.
- **C — telemetry-derived:** the retrieval with no store write.

3,000 memories, 50 of them captured "now" and the rest 30 days earlier so the capture-age exclusion has
something to exclude, plus one deliberately singular memory every concurrent worker retrieves. Two query
shapes: a broad one returning the full 50-row limit (the real write amplification of one retrieval) and a
hot one returning exactly one memory (the contention shape). Latency is p50/p95 over 100 iterations after
20 warmups. Plans are captured with `SET LOCAL enable_seqscan = off`.

Latency was not expected to settle this, and did not — see below. The decisive measurements are
deterministic: `ctid` movement on the recalled row, and a second writer for the same memory under
`lock_timeout`.

## Results

### Latency does not discriminate

| Placement | p50 (ms) | p95 (ms) | p95 delta vs C (ms) |
|---|---|---|---|
| C — telemetry-derived (no store writes) | 6.113 | 7.021 | 0.000 |
| A — counter on the memory row | 7.148 | 7.851 | 0.830 |
| B — append-only records | 7.426 | 8.236 | 1.214 |

A 50-memory retrieval costs ~1 ms p95 to record either way, against a ~7 ms retrieval. The spread between
A and B is smaller than the spread between repeat runs of the same option, so **wall clock alone cannot
choose between them** — which is why LADR-02 nominated the concurrency case instead. Throughput under 16
concurrent workers hitting the same memory (604 ops/s for A, 666 ops/s for B) points the same way as the
probes below but is likewise not decisive on its own.

### Serialisation — the discriminator NFR-02 named

A first writer holds an uncommitted feedback write for one memory; a second connection attempts the same
write with `lock_timeout = 250ms`.

| Placement | Second writer | SQL state |
|---|---|---|
| B — append-only records | proceeded | none |
| A — counter on the memory row | **blocked** | `55P03` `lock_not_available` |

A popular memory under option A is a queue: the second reader waits for the first reader's transaction.
This is the failure NFR-02 exists to prevent, and it is a property of the placement, not of load.

### The read rewrites the row

200 recalls of one memory, `ctid` (the physical tuple address) sampled before and after:

| Placement | ctid before | ctid after | row rewritten | n_dead_tup delta |
|---|---|---|---|---|
| B — append-only records | `(83,51)` | `(83,51)` | no | 57 |
| A — counter on the memory row | `(83,51)` | `(83,201)` | **yes** | 59 |

`ctid` is the decisive column. `n_dead_tup` is reported but not relied on: statistics flush per backend, so
a pooled connection from an earlier phase lands dead tuples inside a later window — which is exactly what
the similar numbers in that column show. Under A every read produces a new row version of the store's
hottest table, and the vacuum load that follows is carried by the read path.

### Read-path isolation

- Results are byte-identical, in the same order, with feedback written and without.
- An injected feedback failure (`42P01 relation "recall_feedback_absent" does not exist`) leaves the
  retrieval returning its results unchanged.

### Actionability — the three tuning questions, against option B

- **Never recalled:** 2,940 of 3,000, exactly the untouched set; the 10 recalled memories are absent and
  the 50 recently captured are excluded by the capture-age clause. Removing that clause lists all 50,
  confirming the clause is what excludes them.
- **How often retrieval returns nothing:** 7 of 8 retrievals in the window, matching the seeded mix.
- **Recency:** `max(occurred_on)` for a given memory answers when it was last recalled.
- **Reset:** truncation leaves no records, so a tuning baseline can start clean.

### Growth

182 bytes per record including indexes; one 50-memory retrieval writes ~8.9 KiB.

| Retrievals/day | 30-day retained | 90-day retained |
|---|---|---|
| 200 | 52 MiB | 156 MiB |
| 2,000 | 521 MiB | 1,562 MiB |
| 20,000 | 5,208 MiB | 15,623 MiB |

### Trigger audit

`append_only_guard` fires on `memory_version` and `group_description` only. The sole trigger on `memory` is
`trg_memory_graph_cascade`, which is `BEFORE DELETE`. A separate feedback table therefore introduces no
coupling to the append-only guarantee or the version chain, and the prototype table carries no trigger of
its own.

## Decision

**Selected: B — append-only records.** A is eliminated twice over and C once.

- **A fails NFR-02 on measurement** — it serialises a second reader of the same memory (`55P03`) and
  rewrites the row on every read.
- **A cannot answer recency**, which LADR-02 named as the deciding question: a count cannot distinguish
  recalled-once-last-year from recalled-weekly.
- **A cannot record a miss at all.** LADR-02 does not name this, and it is the stronger argument: a counter
  lives on a memory row, and a retrieval that returned nothing has no memory row to count against. LADR-01
  requires both halves, so A cannot implement the decision it would serve.
- **C remains viable but pays for a latency advantage that is not measurable** (~1 ms p95 on a ~7 ms
  retrieval) with a dependency on an observability retention policy owned elsewhere, and with
  never-recalled becoming a comparison performed outside the store. Rejected on that trade, not on cost.

## Findings that change the record shape

1. **A per-retrieval correlation identifier is required.** One retrieval returning 50 memories writes 50
   records; without a shared `retrieval_id` those 50 are indistinguishable from 50 retrievals and NFR-03's
   second question ("how often does retrieval return nothing") is unanswerable. A generated identifier
   carries no content, so this stays inside LADR-03.
2. **"Recently captured" is not a column on the thing being measured.** The stable `memory` row carries no
   timestamp; capture age has to come from `min(memory_version.created_on)`. As a correlated subquery it
   dominates the never-recalled plan (25,414 of 25,485 total cost), so NFR-03's never-recalled list should
   express it as a join rather than a subquery when that query is implemented.
3. **The classification column must be schema-constrained.** The prototype `CHECK` rejects an out-of-set
   value, satisfying NFR-01's "constrained, not conventional" criterion. A convention would not.

## Reopening threshold

Re-measure before reusing this evidence if the retrieval limit rises materially (write amplification is
per returned memory), if feedback is ever proposed as an input to ranking, or if the serialisation probe
stops showing `55P03` for the counter placement — the last would mean the measured basis for eliminating A
no longer holds.

## Applies To

LADR-02 (resolved by this evidence); NFR-02 (the constraint that decided it). NFR-01 and NFR-03 are
touched only as far as the placement had to satisfy them; their full verification belongs with the shipped
write path and query surfaces.
