# Testing Strategy

## Test Levels

| Level | Label | Projects | Containers? | Description |
|---|---|---|---|---|
| L0 | Unit | `*.UnitTest` | None | Isolated logic, no I/O — pure in-process |
| L1 | Component | `Application.ComponentTest`, `Infrastructure.ComponentTest` | PostgreSQL + MinIO | End-to-end within a layer; real DB and object storage via Aspire |
| L2 | Integration | `Host.IntegrationTest` | PostgreSQL + MinIO | Full stack via `WebApplicationFactory` + Aspire containers |
| — | Benchmark | `Infrastructure.ComponentTest` (env-gated) | PostgreSQL | `Nfr02BenchmarkTests` — seeds 3,000 memories / 10,000 edges, measures three traversal shapes and captures `EXPLAIN` for each. Skipped unless `SMOOTH_AGE_BENCH=1`, so the PR gate stays fast |
| — | Operational | `scripts/` | Docker | Not tests. Backup/restore round-trip and Postgres pre-upgrade checks, run by hand against a container |

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

It measures through the shipped code path (`IMemoryTraversal`, `IMemoryGraph`) and `EXPLAIN`s the statement
those methods build — a benchmark that plans a hand-written copy measures the copy.

## Test Infrastructure

Shared fixtures live in `tests/SmoothAiProductContextMemory.TestFramework/`. Container orchestration (PostgreSQL, Redis, WireMock, MinIO) lives in `tests/SmoothAiProductContextMemory.TestFramework.Aspire/`.

### AspireFixture

`AspireFixture` provisions and shares test containers across all test assemblies in a process. It tries three strategies in order:

1. **Reuse** — if another fixture in the same process already initialised, adopt the shared state
2. **Fixed endpoints** — probe `127.0.0.1:15432` (Postgres), `127.0.0.1:16379` (Redis), `127.0.0.1:19091` (WireMock) and `127.0.0.1:9002` (MinIO) — succeeds if containers are pre-warmed (CI or local `dotnet run --project tests/SmoothAiProductContextMemory.TestFramework.Aspire`)
3. **Container port discovery** — query `docker`/`podman port` for the persistent named containers (`mimisbrunnr-testcontainer-postgres`, `mimisbrunnr-testcontainer-redis`, `mimisbrunnr-testcontainer-wiremock`, `mimisbrunnr-testcontainer-blob`)
4. **Start Aspire host** — provision fresh containers (takes ~30s on first run)

Container lifetimes are `Persistent` — they survive test runs and are reused on subsequent runs.

### Blob (MinIO) isolation

`AspireFixture` exposes `BlobEndpoint`, `BlobAccessKey` and `BlobSecretKey` (test-fixed credentials). L1
storage tests build an `S3BlobStorage` per test with a **unique bucket name** — the adapter creates the
bucket lazily on first write and treats a missing bucket as a missing object, so each test is isolated
exactly as Respawn isolates the database. There is no shared state between tests' buckets.

### WebAppFixture&lt;T&gt;

Base class for L2 integration tests. Initialises `AspireFixture`, then boots `WebApplicationFactory<TProgram>`. Override hooks:

- `EnrichConfigurationAsync(overrides)` — inject connection strings, WireMock base URL, etc.
- `PostInitializeAsync()` — run post-boot setup (e.g. trigger a sync cycle)
- `RecreateDatabaseOnInitialize` — set `true` to drop/recreate the database before the fixture starts
- `DatabaseName` — default is a Guid-suffixed name for isolation; override for deterministic names

### SmoothAiProductContextMemoryTestDatabase

Factory for per-test isolated databases in L1 Infrastructure tests. Drops/recreates a named database and returns a connection string handle. When EF Core migrations are added, extend `CreateAsync` to run migrations before returning.

### DatabaseResetter

Wraps Respawn for fast between-test data wipes without drop/recreate. Use in `IAsyncLifetime.DisposeAsync()` or an `AfterTest` hook.

### WireMockAdminClient

HTTP client for the WireMock admin API. Obtain via `AspireFixture.CreateWireMockAdminClient()`:

```csharp
await using var admin = aspire.CreateWireMockAdminClient();
await admin.StubJsonResponseAsync("GET", "/api/users", new[] { ... });
// ... run test ...
await admin.ResetAsync(); // clear stubs between tests
```

## Container Port Map

| Container | Local Port | Service |
|---|---|---|
| `mimisbrunnr-testcontainer-postgres` | 15432 | PostgreSQL (`docker.io/apache/age:release_PG17_1.7.0`) |
| `mimisbrunnr-testcontainer-redis` | 16379 | Redis |
| `mimisbrunnr-testcontainer-wiremock` | 19091 | WireMock HTTP admin + stubbed endpoints |
| `mimisbrunnr-testcontainer-blob` | 9002 (s3), 19092 (console) | MinIO S3-compatible object storage |

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
