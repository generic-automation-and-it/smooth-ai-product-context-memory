# APPHOST_AGENTS.md

## TL;DR

Aspire AppHost orchestrating local dev dependencies (PostgreSQL+AGE + MinIO blob storage + Seq), the
`Host` project, and the Aspire dashboard. Run this for the F5 dev experience; **not** used by tests —
`tests/SmoothAiProductContextMemory.TestFramework.Aspire` owns test-fixture orchestration on different
ports/container names.

## Non-Negotiables

- **Dev-only.** Never referenced by test projects, never invoked from CI. CI uses `TestFramework.Aspire`
  via the integration tests.
- **No collision with TestFramework.Aspire.** Runtime artifacts start with `smooth-mimisbrunnr-*`;
  ports must not equal those of the test fixture (Postgres `15432`, Redis `16379`, WireMock `19091`,
  MinIO `9002` s3 / `19092` console). Docker names are ASCII — brand is Mímisbrunnr; `í` is illegal.
- **Container runtime agnostic, and runtime selection is Aspire's job.** Registering containers through
  Aspire means the same AppHost runs against Docker (the default) or Podman with no code or config
  change; set `DOTNET_ASPIRE_CONTAINER_RUNTIME=podman` to switch. Verified working under both. Do not
  add runtime-specific wiring to the AppHost. Container images are **registry-qualified and pinned**
  (`docker.io/apache/age:release_PG17_1.7.0`, `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z`) because Podman refuses to resolve short names
  non-interactively unless the host's `registries.conf` happens to allow it.
- **AppHost is the orchestrator, not a published image.** Starting it pulls the Host image and starts
  postgres/blob/seq. Do not containerise the AppHost. The Host stays runnable as a plain `Program`
  (`WebApplicationFactory<Program>` integration tests must keep working without an AppHost).
- **Default Host is the published GHCR image** (`HostConfiguration:Image`). Set
  `HostConfiguration:UseProject=true` (or `HostConfiguration__UseProject=true`) to compile and run
  `Projects.SmoothAiProductContextMemory_Host` from source instead. Do not delete `AddProject`.
- **Host container is `smooth-mimisbrunnr-host`**, with the same `com.docker.compose.project=smooth-mimisbrunnr` /
  `com.docker.compose.service` labels as postgres/blob/seq. It injects
  `ConnectionStrings__SmoothAiProductContextMemory` (the key Infrastructure reads) plus the existing
  `BlobStorage__*` env vars. GHCR references use `ImagePullPolicy.Always`.
- **Connection-string keys must match what the Host consumes.** Aspire `.WithReference(db)` injects
  `ConnectionStrings:<resource>` automatically for Postgres and Seq. The blob container is **not** a
  connection-string resource: the Host receives `BlobStorage__Endpoint` (from the blob `s3` endpoint)
  plus `BlobStorage__AccessKey` / `BlobStorage__SecretKey` / `BlobStorage__Bucket` as plain environment
  variables, bound by `BlobStorageOptions`.
- **No WireMock in dev AppHost.** The AppHost orchestrates real Postgres/MinIO/Seq only. There is no
  upstream HTTP API to stub in this service.
- **Postgres and MinIO use persistent named volumes.** Dev data survives container restarts.
- **Postgres image is the AGE-bearing pin** `docker.io/apache/age:release_PG17_1.7.0` (Postgres 17 + AGE 1.7.0), not Aspire's `library/postgres:17.6`. Same major as the previous default, so the named volume is compatible. A major mismatch against `smooth-mimisbrunnr-postgres-data` refuses to start and looks like a broken image — drop that volume only if the major actually changed. Recreate `smooth-mimisbrunnr-postgres` once after the image pin so the persistent container is not still running the old image. The 2026-09-13 rename from `smooth-project-memory-*` is a new volume; old data is not attached.
- **Every dev container carries `com.docker.compose.project` / `com.docker.compose.service` labels**
  so Docker Desktop groups them under the `smooth-mimisbrunnr` project while keeping the explicit
  `smooth-mimisbrunnr-*` container names.

## Resource Names

| Resource | Name | Fixed local port | Container name |
|---|---|---:|---|
| PostgreSQL + AGE | `postgres` (`docker.io/apache/age:release_PG17_1.7.0`) | `5432` | `smooth-mimisbrunnr-postgres` |
| Database | `app` | (via `postgres`) | n/a |
| MinIO (blob) | `blob` | `9000` (s3), `9001` (console) | `smooth-mimisbrunnr-blob` |
| Seq | `seq` | `5341` | `smooth-mimisbrunnr-seq` |
| API image (default) | `host` (`HostConfiguration:Image`) | `5141` | `smooth-mimisbrunnr-host` |
| API project (opt-in) | `host` (`HostConfiguration:UseProject=true`) | `5141` http / `7141` https | n/a (host process) |

Test fixture (separate AppHost) uses `15432` / `mimisbrunnr-testcontainer-postgres` — see
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
  (`AddPostgresResource`, `AddBlobResource`, `AddSeqResource`, `AddHostProject` / `AddHostContainer`).
- `HostConfiguration:Image` defaults to `ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory:latest`.
  The AppHost splits on the last colon after the last slash so Aspire `AddContainer(name, image, tag)`
  gets a registry-qualified name. Digest references (`@sha256:`) are not supported. `UseProject=true`
  ignores the image and runs source.
- OpenTelemetry uses the Aspire dashboard's built-in OTLP endpoint supplied to project resources by the
  AppHost runtime. Do not override `OTEL_EXPORTER_OTLP_ENDPOINT` from AppHost unless intentionally
  diverting telemetry away from the dashboard.
- Aspire dashboard URL is printed at startup via the `WriteDashboardStartupHint` extension; use Aspire's
  printed `/login?t=...` URL for the first terminal-driven browser visit.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-13 | Docker Desktop group `smooth-mimisbrunnr`, containers `smooth-mimisbrunnr-{host,postgres,blob,seq}`. Default AppHost run pulls the published Host image; `UseProject=true` keeps source. AppHost itself is not published. Tests stay `mimisbrunnr-testcontainer-*`. | release-image |
| 2026-09-13 | Pin Postgres to `docker.io/apache/age:release_PG17_1.7.0` (same major as Aspire 13.3.0's `library/postgres:17.6`). Persistent container must be recreated once so it is not still the old image. | HLD-003 |
| 2026-09-12 | Pin MinIO to last community release `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z`. Upstream archived the repo and Docker Hub `minio/minio` is no longer publicly pullable (registry returns UNAUTHORIZED), so images must come from quay.io. | PR #17 |
| 2026-09-01 | Aligned the blob ports across code, `appsettings.json` and this document (s3 `9000`, console `9001`; the leftover SeaweedFS `8333` is gone), made the console port configurable, registry-qualified the MinIO image for Podman, and corrected the false claim that the blob resource injects `ConnectionStrings:blob`. Docker/Podman startup verified end to end. | — |
| 2026-08-30 | Created — Aspire AppHost orchestrating Postgres + MinIO blob storage + Seq for local dev, mirroring the `builder-catalogue` house style. No ChatHost (project not yet in tree). | — |
