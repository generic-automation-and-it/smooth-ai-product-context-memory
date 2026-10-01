# Testing Strategy

## Test Levels

| Level | Label | Projects | Containers? | Description |
|---|---|---|---|---|
| L0 | Unit | `*.UnitTest` | None | Isolated logic, no I/O — pure in-process |
| L1 | Component | `Application.ComponentTest`, `Infrastructure.ComponentTest` | PostgreSQL + MinIO | End-to-end within a layer; real DB and object storage via Aspire |
| L2 | Integration | `Host.IntegrationTest` | PostgreSQL + MinIO | Full stack via `WebApplicationFactory` + Aspire containers |
| — | Benchmark | `Infrastructure.ComponentTest` **and** `Application.ComponentTest` (env-gated) | PostgreSQL | `Nfr02BenchmarkTests` — seeds 3,000 memories / 10,000 edges, measures three traversal shapes and captures `EXPLAIN` for each. Skipped unless `SMOOTH_AGE_BENCH=1`, so the PR gate stays fast |
| — | Operational | `scripts/` | Docker | Not tests. Backup/restore round-trip and Postgres pre-upgrade checks, run by hand against a container |

### Env-gated evidence harnesses — all six variables, seven classes

Every one is skipped unless its variable is set, so the PR gate stays fast. The catalogue in the root
`AGENTS.md` is the canonical list; this is the same six with their evidence homes. Six variables gate
seven classes, because `SMOOTH_AGE_BENCH` runs two of them.

| Variable | Filter | Evidence it produces |
|---|---|---|
| `SMOOTH_AGE_BENCH=1` | `Nfr02BenchmarkTests` **and** `TicketTraversalBenchmarkTests` (both share this gate) | HLD-003 NFR-02 traversal measurements |
| `SMOOTH_FTS_BENCH=1` | `RecallTuningEvidenceTests` | HLD-001 NFR-02 recall tuning |
| `SMOOTH_FEEDBACK_BENCH=1` | `FeedbackPlacementEvidenceTests` | HLD-004 LADR-02 feedback placement |
| `SMOOTH_SNAPSHOT_BENCH=1` | `SnapshotEvidenceTests` | HLD-006 NFR-04 snapshot timing |
| `SMOOTH_DOSSIER_BENCH=1` | `DossierWorkflowBenchmarkTests` (`Application.ComponentTest`) | HLD-005 end-to-end dossier timing |
| `SMOOTH_NFR_BENCH=1` | `NfrEvidenceTests` | HLD-004 NFR-01..03 evidence |

### Benchmarks are evidence, not gates

`Nfr02BenchmarkTests` produces figures committed to
`docs/hlds/003-graph-edges-on-age/nfrs/NFR-02-traversal-measurements.md`. It asserts absolute p95 targets **and
the query plan**, because at seed volume almost anything is fast: the first run of this benchmark passed its
10 ms target while sequentially scanning the edge table. A benchmark that checks only wall clock would have
shipped that.

```bash
SMOOTH_AGE_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest \
    --filter Nfr02BenchmarkTests --logger "console;verbosity=detailed"
```

> **A benchmark gate is legitimate; a boolean-correctness gate is not.** `Nfr02BenchmarkTests` asserts
> p95 *and* the query plan, which is a measurement. `TicketTraversalBenchmarkTests` — the second class
> sharing the `SMOOTH_AGE_BENCH` gate — asserts a p95 target the same way, so the rule in this
> callout covers it as well. Of the other five, three also assert a wall-clock
> target — `SnapshotEvidenceTests` against HLD-006 NFR-04's ≤ 9m snapshot ceiling, and
> `FeedbackPlacementEvidenceTests` / `NfrEvidenceTests` against a 2 ms per-retrieval write budget — while
> `DossierWorkflowBenchmarkTests` asserts countable invariants (manifest reconciliation, no-match) and
> `RecallTuningEvidenceTests` asserts nothing at all, recording the precision/recall table, the plans and
> the percentiles. Each harness in the table above is what produces the measurement its evidence document
> records, so one skipped in an environment where it matters leaves that number unevidenced rather than
> failing. That is why the gates are catalogued rather than incidental.

It measures through the shipped code path (`IMemoryTraversal`, `IMemoryGraph`) and `EXPLAIN`s the statement
those methods build — a benchmark that plans a hand-written copy measures the copy.

## Test Infrastructure

Shared fixtures live in `tests/SmoothAiProductContextMemory.TestFramework/`. Container orchestration (PostgreSQL, MinIO) lives in `tests/SmoothAiProductContextMemory.TestFramework.Aspire/`.

### AspireFixture

`AspireFixture` provisions and shares test containers across all test assemblies in a process. It tries three strategies in order:

1. **Reuse** — if another fixture in the same process already initialised, adopt the shared state
2. **Fixed endpoints** — probe `127.0.0.1:15432` (Postgres) and `127.0.0.1:9002` (MinIO) — succeeds if containers are pre-warmed (CI or local `dotnet run --project tests/SmoothAiProductContextMemory.TestFramework.Aspire`)
3. **Container port discovery** — query `docker`/`podman port` for the persistent named containers (`mimisbrunnr-testcontainer-postgres`, `mimisbrunnr-testcontainer-blob`)
4. **Start Aspire host** — provision fresh containers (takes ~30s on first run)

Container lifetimes are `Persistent` — they survive test runs and are reused on subsequent runs.

### Blob (MinIO) isolation

`AspireFixture` exposes `BlobEndpoint`, `BlobAccessKey` and `BlobSecretKey` (test-fixed credentials). L1
storage tests build an `S3BlobStorage` per test with a **unique bucket name** — the adapter creates the
bucket lazily on first write and treats a missing bucket as a missing object, so each test is isolated
by bucket. There is no shared state between tests' buckets.

### WebAppFixture&lt;T&gt;

Base class for L2 integration tests. Initialises `AspireFixture`, then boots `WebApplicationFactory<TProgram>`. Override hooks:

- `EnrichConfigurationAsync(overrides)` — inject connection strings, external-service URLs, etc.
- `PostInitializeAsync()` — run post-boot setup (e.g. trigger a sync cycle)
- `RecreateDatabaseOnInitialize` — set `true` to drop/recreate the database before the fixture starts
- `DatabaseName` — default is a Guid-suffixed name for isolation; override for deterministic names

### SmoothAiProductContextMemoryTestDatabase

Factory for per-test isolated databases in L1 Infrastructure tests. Drops/recreates a named database and returns a connection string handle. When EF Core migrations are added, extend `CreateAsync` to run migrations before returning.

> **Respawn is gone, and so is the `DatabaseResetter` that wrapped it** (removed 2026-09-29). It had no
> callers: every L1 test isolates by creating a fresh database, not by wiping one, so the package
> reference and the wrapper were both dead weight. Isolation here is *per-database*, not per-test-wipe —
> which is why a constraint test never sees a neighbour's rows. Do not reintroduce Respawn expecting the
> isolation it provided; a per-test database is what the suites actually rely on.

## Container Port Map

| Container | Local Port | Service |
|---|---|---|
| `mimisbrunnr-testcontainer-postgres` | 15432 | PostgreSQL (`docker.io/apache/age:release_PG17_1.7.0`) |
| `mimisbrunnr-testcontainer-blob` | 9002 (s3), 19092 (console) | MinIO S3-compatible object storage |

The test dependency set is **PostgreSQL + MinIO only**. The Redis cache and WireMock stubbing
containers were removed — no test consumed the Redis connection string, and WireMock's only consumer
was a placeholder test that has since been deleted. The `AspireFixture` endpoint probing gates on
Postgres and the blob container only.

## Collection Fixture Pattern

The `"Aspire"` xUnit collection shares one `AspireFixture` instance across all tests in a given assembly. Each L1/L2 test assembly must re-declare the collection:

```csharp
// AspireCollection.cs (in each L1/L2 test project)
[CollectionDefinition("Aspire")]
public sealed class AspireCollection : ICollectionFixture<AspireFixture>;
```

Tests opt in via `[Collection("Aspire")]` and receive `AspireFixture` via constructor injection.

## Running Tests

```bash
# All tests (L0 + L1 + L2) — requires Docker
dotnet test SmoothAiProductContextMemory.slnx

# L0 only — no containers required
dotnet test tests/SmoothAiProductContextMemory.Domain.UnitTest
dotnet test tests/SmoothAiProductContextMemory.Application.UnitTest
dotnet test tests/SmoothAiProductContextMemory.Infrastructure.UnitTest
dotnet test tests/SmoothAiProductContextMemory.Host.UnitTest
dotnet test tests/SmoothAiProductContextMemory.AppHost.UnitTest

# L1 component tests
dotnet test tests/SmoothAiProductContextMemory.Application.ComponentTest
dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest

# L2 integration tests
dotnet test tests/SmoothAiProductContextMemory.Host.IntegrationTest

# Pre-warm containers (speeds up first test run)
dotnet run --project tests/SmoothAiProductContextMemory.TestFramework.Aspire
```
