# APPHOST_AGENTS.md

## TL;DR

Aspire AppHost orchestrating local dev dependencies (PostgreSQL+AGE + MinIO blob storage + Seq), the
`Host` project, and the Aspire dashboard. Run this for the F5 dev experience; **not** used by tests —
`tests/SmoothAiProductContextMemory.TestFramework.Aspire` owns test-fixture orchestration on different
ports/container names.

## Non-Negotiables

- **Dev-only.** Never referenced by test projects, never invoked from CI. CI uses `TestFramework.Aspire`
  via the integration tests.
- **Docker naming: accent in the group, ASCII in the artifacts.** The Docker Desktop group is the
  `com.docker.compose.project` **label**, so it carries the brand spelling — `smooth-mímisbrunnr`.
  Container and volume names cannot: Docker rejects non-ASCII outright (`Invalid container name
  (mímisbrunnr-…), only [a-zA-Z0-9][a-zA-Z0-9_.-] are allowed`), so they are transliterated —
  `mimisbrunnr-postgres`, `mimisbrunnr-blob-well`, `mimisbrunnr-seq`, `mimisbrunnr-host`. The test fixture
  follows the same split (`smooth-mímisbrunnr-testing` group, `mimisbrunnr-testcontainer-*` containers).
  Do not "fix" the group label to ASCII, and do not add the accent to a container or volume name. The
  MinIO bucket `smooth-mimisbrunnr-memory-well` is transliterated for the same reason — S3 bucket names
  are DNS labels (lowercase ASCII, digits, hyphens).
- **No collision with TestFramework.Aspire.** Container names are `mimisbrunnr-*` (no `testcontainer` segment);
  ports must not equal those of the test fixture (Postgres `15432`, Redis `16379`, WireMock `19091`,
  MinIO `9002` s3 / `19092` console).
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
- **Host container is `mimisbrunnr-host`**, with the same `com.docker.compose.project=smooth-mímisbrunnr` /
  `com.docker.compose.service` labels as postgres/blob/seq. It injects
  `ConnectionStrings__SmoothAiProductContextMemory` (the key Infrastructure reads) plus the existing
  `BlobStorage__*` env vars. GHCR references use `ImagePullPolicy.Always`.
- **Connection-string keys must match what the Host consumes.** Aspire `.WithReference(db)` injects
  `ConnectionStrings:<resource>` automatically for Postgres and Seq — so the **resource name is the
  connection-string key**. The database resource is therefore named `SmoothAiProductContextMemory`
  (with `databaseName: "app"` keeping the physical database name, so a retained volume's data stays
  valid — the Mímisbrunnr volume rename still starts a fresh volume; see changelog). Naming it `app`
  published `ConnectionStrings__app`, which nothing consumed: the Host threw at
  DI resolve and the `host` resource never started. Rename the resource and you rename the key.
  The blob container is **not** a connection-string resource: the Host receives `BlobStorage__Endpoint` (from the blob `s3` endpoint)
  plus `BlobStorage__AccessKey` / `BlobStorage__SecretKey` / `BlobStorage__Bucket` as plain environment
  variables, bound by `BlobStorageOptions`.
- **No WireMock in dev AppHost.** The AppHost orchestrates real Postgres/MinIO/Seq only. There is no
  upstream HTTP API to stub in this service.
- **Postgres and MinIO use persistent named volumes.** Dev data survives container restarts.
- **Postgres image is the AGE-bearing pin** `docker.io/apache/age:release_PG17_1.7.0` (Postgres 17 + AGE 1.7.0), not Aspire's `library/postgres:17.6`. Same major as the previous default, so the named volume is compatible. A major mismatch against `mimisbrunnr-postgres-data` refuses to start and looks like a broken image — drop that volume only if the major actually changed. Recreate `mimisbrunnr-postgres` once after the image pin so the persistent container is not still running the old image.
- **Every dev container carries `com.docker.compose.project` / `com.docker.compose.service` labels**
  so Docker Desktop groups them under the `smooth-mímisbrunnr` project while keeping the explicit
  `mimisbrunnr-*` container names.

## Resource Names

| Resource | Name | Fixed local port | Container name |
|---|---|---:|---|
| PostgreSQL + AGE | `postgres` (`docker.io/apache/age:release_PG17_1.7.0`) | `5432` | `mimisbrunnr-postgres` |
| Database | `SmoothAiProductContextMemory` (physical DB `app`) | (via `postgres`) | n/a |
| MinIO (blob) | `blob` | `9000` (s3), `9001` (console) | `mimisbrunnr-blob-well` |
| Seq | `seq` (volume `mimisbrunnr-seq-data`) | `5341` | `mimisbrunnr-seq` |
| API image (default) | `host` (`HostConfiguration:Image`) | `5141` | `mimisbrunnr-host` |
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
  `mc`/init container is required. The dev bucket is `smooth-mimisbrunnr-memory-well`; renaming it
  orphans existing objects, because the database stores content addresses that are object keys
  *within* a bucket — treat any future change as a data migration, not a rename.
- `Program.cs` is intentionally thin and functionally chains
  `builder.AddSmoothAiProductContextMemoryAppHostResources().Build().Run()`.
- `DistributedApplicationBuilderExtensions` keeps orchestration split into focused extension methods
  (`AddPostgresResource`, `AddBlobResource`, `AddSeqResource`, `AddHostProject` / `AddHostContainer`).
- `HostConfiguration:Image` defaults to `ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory:latest`.
  The AppHost splits on the last colon after the last slash so Aspire `AddContainer(name, image, tag)`
  gets a registry-qualified name. Digest references (`@sha256:`) are not supported. `UseProject=true`
  ignores the image and runs source.
- **Telemetry is consumed, not just offered.** Aspire injects `OTEL_EXPORTER_OTLP_ENDPOINT` into
  **project** resources automatically and the Host now reads it, so the dashboard's log, trace and
  metric panes are populated. Do not override that variable here unless intentionally diverting
  telemetry away from the dashboard. A resource added with `AddContainer` — e.g. a published image
  instead of the project — does **not** receive it automatically and needs an explicit
  `.WithOtlpExporter()`.
- **Seq is kept, deliberately — and the dashboard is the target, not Seq.** The Aspire dashboard is
  itself an OTLP receiver and needs no help to show logs, traces and metrics; exporting OTel is what
  makes it work. Seq is retained for one reason only: the dashboard's telemetry store is **in-memory,
  capacity-bounded and cleared when the AppHost stops**, so an intermittent failure investigated
  tomorrow is already gone. Seq survives restarts and queries far better. That argument only holds with
  a **data volume** (`mimisbrunnr-seq-data`), which it now has — previously its persistence
  claim was false beyond container removal, unlike Postgres and the object store.
  **Deviation to note:** Seq is fed by the Serilog Seq sink rather than by OTLP ingestion. Seq does
  accept OTLP directly and that would be the tidier wiring, but Serilog is the authoritative log
  pipeline here, and routing logs to two OTLP endpoints (dashboard + Seq) needs a second exporter or a
  collector. Revisit if a collector ever lands.
- **Seq is fed over `ConnectionStrings:seq`**, which is what `.WithReference(seq)` publishes;
  `Aspire.Hosting.Seq` defines no `SEQ_URI` variable. The Host's Serilog Seq sink activates on that key.
- **The `host` resource carries `WithHttpHealthCheck("/health")`**, so the dashboard shows it as healthy
  only once migrations have completed and PostgreSQL is reachable — not merely once the process starts.
- Aspire dashboard URL is printed at startup via the `WriteDashboardStartupHint` extension; use Aspire's
  printed `/login?t=...` URL for the first terminal-driven browser visit.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-13 | Runtime blob container renamed `mimisbrunnr-blob` → `mimisbrunnr-blob-well` (volume `mimisbrunnr-blob-well-data`). Tests stay `mimisbrunnr-testcontainer-blob`. Old volume is orphaned. | release-image |
| 2026-09-13 | Default AppHost run pulls the published Host image as `mimisbrunnr-host` in group `smooth-mímisbrunnr`; `UseProject=true` keeps source. AppHost itself is not published. | release-image |
| 2026-09-13 | Dev MinIO bucket renamed `smooth-project-memory` → `smooth-mimisbrunnr-memory-well`. Safe now because the blob volume was reset by the container rename; a later rename would orphan stored objects. | PR #36 |
| 2026-09-13 | Renamed the dev resources to the Mímisbrunnr brand: group label `smooth-mímisbrunnr` (accented — it is a label), containers and volumes `mimisbrunnr-*` (ASCII — Docker rejects non-ASCII names). The `smooth-project-memory-dev-*` names are gone. Existing containers and volumes are orphaned by the rename and must be removed once. | PR #36 |
| 2026-09-13 | Seq keep-or-drop decided: **kept** for persistence beyond the dashboard's in-memory store, and given the data volume it never had. Fed by the Serilog Seq sink rather than OTLP ingestion — deviation recorded above. | PR #36 |
| 2026-09-13 | Named the database resource after the connection-string key the Host reads (`SmoothAiProductContextMemory`, physical DB still `app`) — as `app` it published `ConnectionStrings__app` and the Host died at DI resolve. Added `WithHttpHealthCheck("/health")`. Corrected the OpenTelemetry claim: telemetry now actually reaches the dashboard, and Seq is fed via `ConnectionStrings:seq` (there is no `SEQ_URI`). | PR #36 |
| 2026-09-13 | Pin Postgres to `docker.io/apache/age:release_PG17_1.7.0` (same major as Aspire 13.3.0's `library/postgres:17.6`). Persistent container must be recreated once so it is not still the old image. | HLD-003 |
| 2026-09-12 | Pin MinIO to last community release `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z`. Upstream archived the repo and Docker Hub `minio/minio` is no longer publicly pullable (registry returns UNAUTHORIZED), so images must come from quay.io. | PR #17 |
| 2026-09-01 | Aligned the blob ports across code, `appsettings.json` and this document (s3 `9000`, console `9001`; the leftover SeaweedFS `8333` is gone), made the console port configurable, registry-qualified the MinIO image for Podman, and corrected the false claim that the blob resource injects `ConnectionStrings:blob`. Docker/Podman startup verified end to end. | — |
| 2026-08-30 | Created — Aspire AppHost orchestrating Postgres + MinIO blob storage + Seq for local dev, mirroring the `builder-catalogue` house style. No ChatHost (project not yet in tree). | — |
