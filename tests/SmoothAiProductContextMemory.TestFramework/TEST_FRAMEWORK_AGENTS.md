# TEST_FRAMEWORK_AGENTS.md

## TL;DR

Shared xunit.v3 test fixtures and helpers reused across the L0/L1/L2 test projects. This is a library (`IsTestProject=false`) — it contains no tests.

## Non-Negotiables

- **Keep it generic and domain-agnostic.** No references to feature code or concrete domain types; fixtures are reusable scaffolding only.
- **No `[Fact]`/`[Theory]` here.** `IsTestProject` is `false`; tests live in the `*.UnitTest` / `*.ComponentTest` / `*.IntegrationTest` projects that reference this one.

## Key Behaviors

- **`AspireFixture`** owns L1/L2 container dependencies, provisioned by `tests/SmoothAiProductContextMemory.TestFramework.Aspire`. It resolves endpoints in three steps: (1) probe the fixed well-known ports of already-running persistent containers, (2) ask the container CLI for the published port of each `project-test-*` container, (3) only then boot the Aspire `DistributedApplication` itself. The `DistributedApplication` is shared process-wide via `[CollectionDefinition("Aspire")]` and disposal is a deliberate no-op — containers are `ContainerLifetime.Persistent` and outlive the test run.
- **Container runtime is Aspire's concern, not the fixture's.** Aspire selects Docker by default and honours `DOTNET_ASPIRE_CONTAINER_RUNTIME=podman` when Podman is preferred. The fixture only shells out for *port discovery*, probing `docker` first and `podman` second; both calls are timeout-bounded and read stdout asynchronously so a stalled CLI (for example Podman with a stopped machine) cannot hang the test run.
- **Test dependency ports** (fixed, must never collide with the dev AppHost's `smooth-project-memory-dev-*` resources):

  | Resource | Container | Port |
  |---|---|---:|
  | PostgreSQL | `project-test-postgres` (`docker.io/apache/age:release_PG17_1.7.0`) | `15432` |
  | Redis | `project-test-redis` | `16379` |
  | WireMock | `project-test-wiremock` | `19091` |
  | MinIO (blob) | `project-test-blob` | `9002` (s3), `19092` (console) |

  Databases provisioned on the test Postgres: `app-component`, `app-integration`, `infra-component`, `infra-integration`, `host-integration`.
- **`WebAppFixture<TProgram>`** wraps `WebApplicationFactory<TProgram>` and is generic over a Host's entry point. Because xunit.v3 compiles test assemblies as executables (each gets its own auto-generated `Program`), an integration test must reference the Host with `Aliases="HostApp"` and close the fixture as `WebAppFixture<HostApp::Program>` to avoid an ambiguous `Program`.
- **`ServiceProviderFixture`** builds an isolated `IServiceCollection`/`IServiceProvider` for L0/L1 tests and routes logging to the test output via `XUnitLoggerFactory`.
- **`SmoothAiProductContextMemoryTestDatabase`** creates a fresh isolated PostgreSQL database per L1 test against the Aspire test Postgres, and **drops it on dispose** so the persistent test container never accumulates orphans. It is domain-agnostic: it creates/drops databases but knows nothing about the application DbContext or migrations — the caller owns migrating the returned database.
- **`XUnitLogger*`** bridges `ILogger` to xunit's `ITestOutputHelper`, with optional per-category minimum levels.
- **`PriorityOrderer` + `[TestPriority]`** order test cases when sequencing matters; opt in with `[TestCaseOrderer(typeof(PriorityOrderer))]` on the test class.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-13 | Test Postgres image pinned to `docker.io/apache/age:release_PG17_1.7.0`. Recreate `project-test-postgres` once after the pin — `ContainerLifetime.Persistent` keeps the previous image until the container is removed. | HLD-003 WT-01 |
| 2026-09-12 | Test Aspire MinIO image pinned to `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z` (same tag as the dev AppHost). CI wait now probes TCP `:9002` before `/minio/health/live` and dumps container logs on timeout. | PR #17 |
| 2026-09-09 | `SmoothAiProductContextMemoryTestDatabase` made domain-agnostic — removed the Infrastructure ProjectReference and the EF wiring (migrations moved to the caller, `PersistenceTestBase`); it now drops the per-test database on dispose so the persistent test Postgres never accumulates orphans. | PR #11 |
| 2026-09-01 | Documented `AspireFixture` endpoint resolution, container-runtime handling and the fixed test dependency ports. Blob console port moved `19192` → `19092` to sit in the test port band; port discovery now probes `docker` before `podman` and is timeout-safe. | — |
| 2026-05-30 | Created — lean fixtures (`ServiceProviderFixture`, `WebAppFixture<TProgram>`), xunit output logging, and test-case ordering helpers. | — |
