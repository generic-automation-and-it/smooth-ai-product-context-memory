# APPHOST_AGENTS.md

## TL;DR

Aspire AppHost orchestrating local dev dependencies (PostgreSQL + MinIO blob storage + Seq), the
`Host` project, and the Aspire dashboard. Run this for the F5 dev experience; **not** used by tests —
`tests/SmoothAiProductContextMemory.TestFramework.Aspire` owns test-fixture orchestration on different
ports/container names.

## Non-Negotiables

- **Dev-only.** Never referenced by test projects, never invoked from CI. CI uses `TestFramework.Aspire`
  via the integration tests.
- **No collision with TestFramework.Aspire.** Container names start with `smooth-project-memory-dev-*`;
  ports must not equal those of the test fixture (Postgres `15432`, Redis `16379`, WireMock `19091`,
  MinIO `9002` s3 / `19092` console).
- **Container runtime agnostic, and runtime selection is Aspire's job.** Registering containers through
  Aspire means the same AppHost runs against Docker (the default) or Podman with no code or config
  change; set `DOTNET_ASPIRE_CONTAINER_RUNTIME=podman` to switch. Verified working under both. Do not
  add runtime-specific wiring to the AppHost. Container images are **registry-qualified**
  (`quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z`) because Podman refuses to resolve short names non-interactively unless the
  host's `registries.conf` happens to allow it.
- **Project-references the Host as `Projects.SmoothAiProductContextMemory_Host`.** The Host stays
  runnable as a plain `Program` (`WebApplicationFactory<Program>` integration tests must keep working
  without an AppHost).
- **Connection-string keys must match what the Host consumes.** Aspire `.WithReference(db)` injects
  `ConnectionStrings:<resource>` automatically for Postgres and Seq. The blob container is **not** a
  connection-string resource: the Host receives `BlobStorage__Endpoint` (from the blob `s3` endpoint)
  plus `BlobStorage__AccessKey` / `BlobStorage__SecretKey` / `BlobStorage__Bucket` as plain environment
  variables, bound by `BlobStorageOptions`.
- **No WireMock in dev AppHost.** The AppHost orchestrates real Postgres/MinIO/Seq only. There is no
  upstream HTTP API to stub in this service.
- **Postgres and MinIO use persistent named volumes.** Dev data survives container restarts.
- **Every dev container carries `com.docker.compose.project` / `com.docker.compose.service` labels**
  so Docker Desktop groups them under the `smooth-project-memory` project while keeping the explicit
  `smooth-project-memory-dev-*` container names.

## Resource Names

| Resource | Name | Fixed local port | Container name |
|---|---|---:|---|
| PostgreSQL | `postgres` | `5432` | `smooth-project-memory-dev-postgres` |
| Database | `app` | (via `postgres`) | n/a |
| MinIO (blob) | `blob` | `9000` (s3), `9001` (console) | `smooth-project-memory-dev-blob` |
| Seq | `seq` | `5341` | `smooth-project-memory-dev-seq` |
| API project | `host` | `5141` http / `7141` https (from `launchSettings`) | n/a (host process) |

Test fixture (separate AppHost) uses `15432` / `project-test-postgres` — see
`tests/SmoothAiProductContextMemory.TestFramework/TEST_FRAMEWORK_AGENTS.md`.

## Key Behaviors

- Postgres password is intentionally **not** committed in `appsettings.json`. Supply
  `PostgresConfiguration:Password` via AppHost User Secrets (`dotnet user-secrets set
  "PostgresConfiguration:Password" "..." --project src/SmoothAiProductContextMemory.AppHost`) or the
  `POSTGRESCONFIGURATION__PASSWORD` environment variable in automated environments.
- Every host port is configuration-driven with a code default: `PostgresConfiguration:Port` (5432),
  `BlobConfiguration:Port` (9000), `BlobConfiguration:ConsolePort` (9001), `SeqConfiguration:Port`
  (5341). `appsettings.json` must keep these values in sync with the code defaults and with the table
  above — a divergence here is invisible until something binds the wrong port.
- MinIO credentials are committed in `appsettings.Development.json` (local dev only, firewall-isolated).
  Bucket creation is lazy: the storage adapter ensures the bucket exists on first write, so no separate
  `mc`/init container is required.
- `Program.cs` is intentionally thin and functionally chains
  `builder.AddSmoothAiProductContextMemoryAppHostResources().Build().Run()`.
- `DistributedApplicationBuilderExtensions` keeps orchestration split into focused extension methods
  (`AddPostgresResource`, `AddBlobResource`, `AddSeqResource`, `AddHostProject`).
- OpenTelemetry uses the Aspire dashboard's built-in OTLP endpoint supplied to project resources by the
  AppHost runtime. Do not override `OTEL_EXPORTER_OTLP_ENDPOINT` from AppHost unless intentionally
  diverting telemetry away from the dashboard.
- Aspire dashboard URL is printed at startup via the `WriteDashboardStartupHint` extension; use Aspire's
  printed `/login?t=...` URL for the first terminal-driven browser visit.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-12 | Pin MinIO to last community release `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z`. Upstream archived the repo and Docker Hub `minio/minio` is no longer publicly pullable (registry returns UNAUTHORIZED), so images must come from quay.io. | PR #17 |
| 2026-09-01 | Aligned the blob ports across code, `appsettings.json` and this document (s3 `9000`, console `9001`; the leftover SeaweedFS `8333` is gone), made the console port configurable, registry-qualified the MinIO image for Podman, and corrected the false claim that the blob resource injects `ConnectionStrings:blob`. Docker/Podman startup verified end to end. | — |
| 2026-08-30 | Created — Aspire AppHost orchestrating Postgres + MinIO blob storage + Seq for local dev, mirroring the `builder-catalogue` house style. No ChatHost (project not yet in tree). | — |
