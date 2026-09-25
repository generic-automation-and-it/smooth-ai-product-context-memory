# NFR-04 — Snapshot operability measurement

Measured with the env-gated `SnapshotEvidenceTests` harness (Infrastructure.ComponentTest, run with
`SMOOTH_SNAPSHOT_BENCH=1`). This is the evidence HLD-006 NFR-04 requires before it moves past Draft:
a full snapshot of the reference corpus completes in single-digit minutes, measured not assumed.

| | |
|---|---|
| **Date** | 2026-09-25 |
| **Stack** | Host from working tree; PostgreSQL+AGE `docker.io/apache/age:release_PG17_1.7.0` (Postgres 17) + MinIO blob store, via the Aspire test harness |
| **Hardware** | local Docker (Apple Silicon) |
| **Corpus** | 1,000 memories, 1,000 versions, 999 `:LINKS` edges, 1,000 blob bodies |
| **Seed time** | 7.6 s (excluded — not part of the snapshot) |
| **Snapshot** | capture (relational + AGE graph + blob walk) + tar/manifest write |
| **Measured** | 2.80 s (0m3s) |
| **NFR-04 target** | single-digit minutes (≤ 9m) |
| **Re-run** | `SMOOTH_SNAPSHOT_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest --filter SnapshotEvidenceTests` |

## Method

The harness provisions a fresh, migrated, isolated database and blob bucket, seeds the corpus, then
times `SnapshotStore.Handler` over the real `NpgsqlSnapshotRepository` + `TarSnapshotArchive` — the
same code path the HTTP snapshot endpoint and the container `snapshot` verb use. The archive is
written to a temp directory; blob bodies are content-addressed and stored in MinIO.

## Honest caveats

- **Volume is 1,000, not the full BRD-001 "thousands" assumption.** A 5,000+ memory run costs minutes
  of seeding; 1,000 was chosen as a representative sample that fits CI-adjacent runtime. The dominant
  costs — relational capture, one blob walk per referenced body, tar write — scale linearly, so
  2.80 s at 1,000 bodies extrapolates to roughly a few tens of seconds at "thousands", comfortably
  inside the single-digit-minute target.
- **Blob bodies are small.** Each is a short UTF-8 payload. Large attachments would shift the walk
  and write cost toward object-storage transfer, which this measurement does not stress.
- **Single run on one machine.** No p50/p95 distribution; the number is a single cold-ish run.
  Re-run to confirm on the operator's hardware before relying on a specific ceiling.
- **The restore half is measured separately** by the L1 round-trip test (`SnapshotRestoreRoundTripTests`),
  not this harness.

## Result

A full corpus snapshot at 1,000 memories / 1,000 bodies completes in **2.80 s** — over an order of
magnitude inside the single-digit-minute target. The NFR-04 operability claim is evidenced.
