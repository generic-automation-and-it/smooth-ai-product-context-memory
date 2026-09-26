# APPHOST_AGENTS.md

## TL;DR

Aspire AppHost orchestrating PostgreSQL+AGE, MinIO, Seq, the API Host, and the Aspire dashboard. Source
mode is the F5 development experience; release mode is packaged as a privileged controller image and
must remain isolated from the test fixture and development installation.

## Non-Negotiables

- **Dev orchestrator.** Never start the development profile from tests or CI. Isolated release-image smoke tests may start the release profile with unique installation names and ports. L0 may reference
  `HostLaunchMode` (`tests/SmoothAiProductContextMemory.AppHost.UnitTest`). Container orchestration
  for tests stays in `TestFramework.Aspire`.
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
  (`docker.io/apache/age:release_PG17_1.7.0`, `cgr.dev/chainguard/minio@sha256:bd014394a80898e68c149f2311fdf8d5a2c2f3bb2c33b9327ae6d02b4b065ae1`) because Podman refuses to resolve short names
  non-interactively unless the host's `registries.conf` happens to allow it.
- **Source mode remains the development default.** Starting it compiles Host from the working tree and
  starts Týr/Iðunn/Saga. The separate release profile may package this same live AppHost, DCP, and
  dashboard into a controller image; it must force API image mode and must not depend on source paths,
  restore, or a host SDK. The Host stays runnable as a plain `Program`
  (`WebApplicationFactory<Program>` integration tests must keep working without an AppHost).
- **Release and development installations never share identity.** Release names, labels, volumes,
  state, and teardown are installation-scoped and distinct from `mimisbrunnr-*` development resources
  and `mimisbrunnr-testcontainer-*` fixtures. A foreign collision fails before mutation.
- **Engine access is an administrative boundary.** The release controller reaches Docker or Podman
  through a configured socket or authenticated endpoint. Never open an unauthenticated engine API,
  describe a read-only socket bind as read-only access, or imply that the controller is sandboxed from
  the engine user.
- **Default Host is the working tree.** `HostConfiguration:UseProject` defaults to `true` and
  `appsettings.json` matches. Image-pull is opt-in: `HostConfiguration:UseProject=false` (or
  `HostConfiguration__UseProject=false`) plus `HostConfiguration:Image`. Do not delete `AddProject`
  or `AddHostContainer`. A GHCR `:latest` tag may lag the working tree — never treat an image-mode
  run as current source.
- **Mode must be visible without reading config.** Startup prints `Host mode: working tree (source).`
  or `Host mode: published image <ref>. Tag may lag the working tree.` Dashboard resource names
  differ: `host-working-tree` vs `host-published-image`. Do not collapse both modes onto resource
  name `host` — that is how a stale image looked healthy.
- **Host container (image mode only) is `mimisbrunnr-host`**, with the same
  `com.docker.compose.project=smooth-mímisbrunnr` / `com.docker.compose.service` labels as
  postgres/blob/seq. It injects `ConnectionStrings__SmoothAiProductContextMemory` (the key
  Infrastructure reads) plus the existing `BlobStorage__*` env vars. GHCR references use
  `ImagePullPolicy.Always`.
- **Connection-string keys must match what the Host consumes.** The dashboard names are independent
  from connection-string keys: `.WithReference(db, connectionName: "SmoothAiProductContextMemory")`
  and `.WithReference(seq, connectionName: "seq")` retain the Host's established configuration while
  the resources are named `mimers-head` and `saga-seq`. Do not omit either `connectionName`: the Host
  would otherwise receive a key derived from the resource name and fail during startup or lose Seq logs.
  The blob container is **not** a connection-string resource: the Host receives `BlobStorage__Endpoint` (from the blob `s3` endpoint)
  plus `BlobStorage__AccessKey` / `BlobStorage__SecretKey` / `BlobStorage__Bucket` as plain environment
  variables, bound by `BlobStorageOptions`.
- **No WireMock in dev AppHost.** The AppHost orchestrates real Postgres/MinIO/Seq only. There is no
  upstream HTTP API to stub in this service.
- **Postgres and MinIO use persistent named volumes.** Dev data survives container restarts.
- **`ContainerLifetime.Persistent` stays the default** on postgres/blob/seq. Captured memories and blobs must survive an AppHost exit. Do not change it to session lifetime to "fix" leftover containers — that is the supported teardown's job.
- **Never glob `mimisbrunnr-*` for teardown.** That prefix also matches `mimisbrunnr-testcontainer-*` (TestFramework.Aspire). Stop and reset use an exact allowlist (`mimisbrunnr-postgres`, `mimisbrunnr-blob-well`, `mimisbrunnr-seq`, `mimisbrunnr-host`) plus the `com.docker.compose.project=smooth-mímisbrunnr` label (does not match `smooth-mímisbrunnr-testing`).
- **Stop and reset are distinct binaries.** `scripts/stop-dev-stack.sh` removes the four allowlisted containers and leaves named volumes. `scripts/reset-dev-stack.sh` is the only volume-destroy path (`mimisbrunnr-postgres-data`, `mimisbrunnr-blob-well-data`, `mimisbrunnr-seq-data`). Do not add a `--volumes` flag to stop. There is no prompt on reset — choosing that command is the explicit ask.
- **Postgres image is the AGE-bearing pin** `docker.io/apache/age:release_PG17_1.7.0` (Postgres 17 + AGE 1.7.0), not Aspire's `library/postgres:17.7`. Same major as the previous default, so the named volume is compatible. A major mismatch against `mimisbrunnr-postgres-data` refuses to start and looks like a broken image — drop that volume only if the major actually changed. Recreate `mimisbrunnr-postgres` once after the image pin so the persistent container is not still running the old image.
- **Every dev container carries `com.docker.compose.project` / `com.docker.compose.service` labels**
  so Docker Desktop groups them under the `smooth-mímisbrunnr` project while keeping the explicit
  `mimisbrunnr-*` container names.

## Resource Names

| Resource | Name | Fixed local port | Container name |
|---|---|---:|---|
| PostgreSQL + AGE | `tyr-postgres` (`docker.io/apache/age:release_PG17_1.7.0`) | `5432` | `mimisbrunnr-postgres` |
| Database | `mimers-head` (physical DB `app`) | (via `tyr-postgres`) | n/a |
| MinIO (blob) | `idunn-blob` | `9000` (s3), `9001` (console) | `mimisbrunnr-blob-well` |
| Seq | `saga-seq` (volume `mimisbrunnr-seq-data`) | `5341` | `mimisbrunnr-seq` |
| API project (default) | `host-working-tree` | `5141` http / `7141` https | n/a (host process) |
| API image (opt-in) | `host-published-image` (`HostConfiguration:UseProject=false`) | `5141` | `mimisbrunnr-host` |

Test fixture (separate AppHost) uses `15432` / `mimisbrunnr-testcontainer-postgres` — see
`tests/SmoothAiProductContextMemory.TestFramework/TEST_FRAMEWORK_AGENTS.md`.

## Key Behaviors

- Context API read/write tokens are secret AppHost parameters injected into both source and image Host
  resources as `ApiAccess__ReadToken` and `ApiAccess__WriteToken`. They are never committed or printed.
  Release configuration requires explicit values; development uses Aspire secret parameters so the
  read and write agents can receive distinct runtime capabilities.

- Postgres password is intentionally **not** committed in `appsettings.json`. Supply
  `PostgresConfiguration:Password` via AppHost User Secrets (`dotnet user-secrets set
  "PostgresConfiguration:Password" "..." --project src/SmoothAiProductContextMemory.AppHost`) or the
  `POSTGRESCONFIGURATION__PASSWORD` environment variable in automated environments.
- Every host port is configuration-driven with a code default: `PostgresConfiguration:Port` (5432),
  `BlobConfiguration:Port` (9000), `BlobConfiguration:ConsolePort` (9001), `SeqConfiguration:Port`
  (5341), `HostConfiguration:Port` (5141, image mode). `appsettings.json` must keep these values in sync with the code defaults and with the table
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
- `HostConfiguration:Image` defaults to `ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory:latest`
  in development and is ignored unless `UseProject=false`. Tags and digest references are supported.
  Release mode requires a sha256 digest; its entrypoint forces image mode and Production configuration.
  The release image includes the runtime; development image mode still needs an AppHost runtime on the host.
- Host launch mode is resolved by `HostLaunchMode` (`DefaultUseProject = true`). A missing
  `HostConfiguration:UseProject` key is working-tree mode, not image mode.
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
- **Seq is fed over `ConnectionStrings:seq`**, which `.WithReference(seq, connectionName: "seq")`
  publishes; `Aspire.Hosting.Seq` defines no `SEQ_URI` variable. The Host's Serilog Seq sink activates on that key.
- **Both Host resources carry `WithHttpHealthCheck("/health")`**, so the dashboard shows them as
  healthy only once migrations have completed and PostgreSQL is reachable — not merely once the
  process starts.
- Aspire dashboard URL is printed at startup via the `WriteDashboardStartupHint` extension; use Aspire's
  printed `/login?t=...` URL for the first terminal-driven browser visit. The same hint states that
  `tyr-postgres` (`mimisbrunnr-postgres`), `idunn-blob` (`mimisbrunnr-blob-well`), and `saga-seq`
  (`mimisbrunnr-seq`) keep running after this process exits, that `mimisbrunnr-host` may remain after a
  hard kill, and names the two teardown scripts. There is no reliable Aspire exit hook under `pkill`
  (SIGKILL), so the asymmetry is surfaced at startup — the moment a developer still has a terminal.
- **Teardown does not kill AppHost, DCP, or `dotnet`.** Exit the AppHost first, then run the script.
  Scripts do not `pkill` DCP (other workspaces may share the machine). Missing `mimisbrunnr-host` is
  success (`UseProject=true` has no Host container). Empty stack is exit 0.
- **AGE-pin recreate is the same Persistent mechanism, different symptom** — see the image-pin
  non-negotiable above. `stop-dev-stack.sh` is the supported way to remove the container so the next
  AppHost start picks up a new image without dropping volumes. Do not invent a third verb.
- Runtime for the scripts is `$DOTNET_ASPIRE_CONTAINER_RUNTIME` (default `docker`), the same env
  Aspire uses. AppHost C# stays runtime-agnostic; do not add Docker/Podman wiring there.

## Architecture Decisions

### LADR-001 — Two named scripts, not one command plus a flag

- **Date:** 2026-09-13 · **Status:** Accepted
- **Context:** An ambiguous `--volumes` / `--force` on a shared teardown can drop the captured corpus.
  That is a worse defect than leftover containers.
- **Decision:** `scripts/stop-dev-stack.sh` (containers only) and `scripts/reset-dev-stack.sh`
  (containers, then the three named volumes). Reset may call stop. No volume flag on stop. No prompt
  on reset — the distinct command is the explicit ask.
- **Consequences:** Two entry points to document. Historical orphan volumes from renames are out of
  both scripts.

### LADR-002 — Persistence survives AppHost exit; teardown is explicit

- **Date:** 2026-09-13 · **Status:** Accepted
- **Context:** Auto-stopping containers on Ctrl+C or process death would undo `ContainerLifetime.Persistent`.
  A hard kill (`pkill`) never runs an exit hook.
- **Decision:** No exit hook that stops containers. Hint at startup. Operator runs a script after exit.
- **Consequences:** Leftover stack until the script runs. DCP is not killed by teardown.

### LADR-003 — Allowlist plus project label, never a name glob

- **Date:** 2026-09-13 · **Status:** Accepted
- **Context:** `mimisbrunnr-*` matches `mimisbrunnr-testcontainer-*`. A careless glob kills the test fixtures.
- **Decision:** Exact container and volume names copied from this AppHost. Refuse if
  `com.docker.compose.project` is not `smooth-mímisbrunnr`.
- **Consequences:** An unlabeled leftover occupying a allowlisted name fails loud (correct). Names must
  stay in sync with the C# constants; a rename here is a rename in the scripts.

### LADR-004 — Dashboard stays in-process

- **Date:** 2026-09-13 · **Status:** Accepted
- **Context:** The dashboard's in-process lifetime is what makes leftover containers visible. A
  standalone `mcr.microsoft.com/dotnet/aspire-dashboard` exists. OTLP containerises cleanly; the
  resource service (`:20290`) is hosted by AppHost. A misconfigured link degrades silently to
  telemetry-only.
- **Decision:** In development, keep the AppHost-managed dashboard. LADR-005 permits packaging it with the release controller, not replacing it with a telemetry-only standalone dashboard.
- **Consequences:** Dashboard dies with AppHost. Seq remains the durable log (`mimisbrunnr-seq-data`).

### LADR-005 — Package the live AppHost as a separate release controller

- **Date:** 2026-09-15 · **Status:** Accepted
- **Context:** Users require a Docker/Podman-only installation in which one pulled controller image
  starts and controls the API and dependencies. Generated Compose output does not retain live Aspire
  resource control, and changing the existing API image would break its standalone API/export contract.
- **Decision:** Publish a second multi-platform image containing the AppHost, DCP, dashboard, .NET
  runtime, and required engine client. Release mode forces a digest-pinned API container and uses an
  installation-scoped identity. The existing API image and source-mode development flow remain intact.
- **Consequences:** This is custom packaging outside Aspire's standard deployment path. Engine access
  is privileged, runtime networking and recovery require end-to-end proof, and public aliases must not
  be promoted until the exact API/controller candidates pass smoke tests.

## Test References

- L0: `tests/SmoothAiProductContextMemory.AppHost.UnitTest/` — default resolves to working-tree mode;
  explicit `true`/`false`; working-tree announcement does not say `published image`.
- Engine-free lifecycle: `scripts/test-apphost-entrypoint.py` after building AppHost; validates all ownership/configuration before mutation and waits for child shutdown.
- Release policy: `python3 scripts/test_release_policy.py`.
- Isolated container smoke: `scripts/smoke-apphost-container.sh IMAGE`; uses synthetic data and installation-scoped cleanup. Never mount a host Docker config that requires a platform-specific credential helper.

## Release Controller Contract

- Bind-address validation applies only in release mode; development ignores this unused endpoint override.
- Canonical name for every `run`, `stop`, and `reset` container is `mimisbrunnr-<id>-controller`; retain engine-generated hostname. Remove a stopped controller explicitly before replacing it. Do not invoke maintenance through `docker exec` on a live controller.
- Preflight uses `--validate-configuration` without starting Aspire. All existing containers and volumes must pass ownership checks before mutation. Inspection errors fail closed.
- Workload shutdown is graceful and API-first for entrypoint cleanup; allow 180 seconds for controller stop. Aspire performs its own session cleanup before fallback cleanup. Forced termination can leave workloads; next start reconciles them without dropping volumes.
- `EngineConfiguration__BindAddress` is mandatory for release: explicit non-wildcard engine interface IP. Docker Desktop can use `127.0.0.1`; Linux bridge deployment requires a reachable engine-side interface (for example bridge gateway). `EngineConfiguration__HostAddress` is independently advertised to the controller. Do not expose unauthenticated API/Seq on public interfaces.
- Engine access defaults to Unix socket. TCP requires `DOCKER_TLS_VERIFY=1` and appropriate mutual-TLS credentials; transport support is not equivalent to end-to-end platform validation.
- Docker Desktop ARM64 lifecycle/data smoke passed locally. Podman, secured TCP, native Linux matrix, dashboard commands/all telemetry signals, and cross-version upgrades remain release acceptance gaps. Do not claim full production readiness from build/unit tests.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-26 | The Host image now creates `/app/.context` (including the default `snapshots` subdir) owned by the non-root app user during build, so a default-user container can write the default snapshot and metadata paths without a pre-created host mount. | HLD-006 |
| 2026-09-26 | The published Host container now gets a `host-context` volume mounted at `/app/.context`, so HTTP snapshots written to `.context/snapshots` survive a host-container restart/removal (and `stop-dev-stack.sh`, which keeps volumes) instead of living in the container's ephemeral writable layer. The project/working-tree mode already wrote to the host workspace. | HLD-006 |
| 2026-09-24 | MinIO image moved to `cgr.dev/chainguard/minio@sha256:bd014394a80898e68c149f2311fdf8d5a2c2f3bb2c33b9327ae6d02b4b065ae1`: quay.io `minio/minio` stopped serving (repo returns 401, tag no longer active), so fresh pulls failed and the CI test host never created its blob container. Chainguard's free tier publishes only `:latest`, hence the digest pin. The image runs non-root and cannot write a volume the old root-run image created, so the dev blob container runs `--user 0:0`; existing `mimisbrunnr-blob-well` data keeps working (verified against a root-owned volume). | PR #99 |
| 2026-09-17 | Added secret Aspire parameters for distinct API read/write tokens and injects them into project and image Host modes. | HLD-002 NFR-04 |
| 2026-09-17 | Approved secret parameter injection for separate context API read/write capabilities. | HLD-002 NFR-04 |
| 2026-09-16 | Limited bind-address validation to release mode and added development-mode regression cases. | PR #65 review |
| 2026-09-16 | Added the configurable image-mode API port to the port contract. | PR #65 review |
| 2026-09-15 | Hardened release preflight, canonical controller ownership, graceful signal handling, explicit bind/advertised addresses, and registry credential isolation; added regression tests and documented remaining runtime verification gaps. | LADR-005 |
| 2026-09-15 | Accepted a separate containerized live-AppHost release controller while preserving source-mode development, the standalone API image, and isolated test orchestration. | LADR-005 |
| 2026-09-14 | Startup hint now pairs each persistent resource name with its docker-visible container name, and the Key Behaviors paraphrase matches. Working-tree Host reference uses the `HostConnectionStringName` constant instead of a literal; the database-resource local and parameters are renamed `postgres` → `database` (the server resource stays `tyr-postgres`). | ai-analyse |
| 2026-09-14 | Dashboard title is `Mímisbrunnr`; resources are `tyr-postgres`, `mimers-head`, `idunn-blob`, and `saga-seq`. Explicit connection names preserve the Host's existing database and Seq configuration. The physical database stays `app`, avoiding a migration of the persistent dev corpus. | AppHost naming |
| 2026-09-14 | Upgraded Aspire AppHost SDK and hosting packages to 13.5.3. AGE remains explicitly pinned to PostgreSQL 17, compatible with Aspire's `library/postgres:17.7` default. | Aspire 13.5.3 |
| 2026-09-13 | Documented Persistent leftover after AppHost exit. Startup hint names `scripts/stop-dev-stack.sh` (keep data) and `scripts/reset-dev-stack.sh` (destroy volumes). Allowlist, never a `mimisbrunnr-*` glob. | AppHost teardown |
| 2026-09-13 | Default AppHost run compiles Host from the working tree; published-image path is opt-in (`UseProject=false`) and announced so a lagging GHCR tag cannot look like current source. | APPHOST_AGENTS.md |
| 2026-09-13 | Runtime blob container renamed `mimisbrunnr-blob` → `mimisbrunnr-blob-well` (volume `mimisbrunnr-blob-well-data`). Tests stay `mimisbrunnr-testcontainer-blob`. Old volume is orphaned. | release-image |
| 2026-09-13 | Default AppHost run pulls the published Host image as `mimisbrunnr-host` in group `smooth-mímisbrunnr`; `UseProject=true` keeps source. AppHost itself is not published. | release-image |
| 2026-09-13 | Dev MinIO bucket renamed `smooth-project-memory` → `smooth-mimisbrunnr-memory-well`. Safe now because the blob volume was reset by the container rename; a later rename would orphan stored objects. | PR #36 |
| 2026-09-13 | Renamed the dev resources to the Mímisbrunnr brand: group label `smooth-mímisbrunnr` (accented — it is a label), containers and volumes `mimisbrunnr-*` (ASCII — Docker rejects non-ASCII names). The `smooth-project-memory-dev-*` names are gone. Existing containers and volumes are orphaned by the rename and must be removed once. | PR #36 |
| 2026-09-13 | Seq keep-or-drop decided: **kept** for persistence beyond the dashboard's in-memory store, and given the data volume it never had. Fed by the Serilog Seq sink rather than OTLP ingestion — deviation recorded above. | PR #36 |
| 2026-09-13 | Named the database resource after the connection-string key the Host reads (`SmoothAiProductContextMemory`, physical DB still `app`) — as `app` it published `ConnectionStrings__app` and the Host died at DI resolve. Added `WithHttpHealthCheck("/health")`. Corrected the OpenTelemetry claim: telemetry now actually reaches the dashboard, and Seq is fed via `ConnectionStrings:seq` (there is no `SEQ_URI`). | PR #36 |
| 2026-09-13 | Pin Postgres to `docker.io/apache/age:release_PG17_1.7.0` (same major as Aspire's then-default `library/postgres:17.6`). Persistent container must be recreated once so it is not still the old image. | HLD-003 |
| 2026-09-12 | Pin MinIO to last community release `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z`. Upstream archived the repo and Docker Hub `minio/minio` is no longer publicly pullable (registry returns UNAUTHORIZED), so images must come from quay.io. | PR #17 |
| 2026-09-01 | Aligned the blob ports across code, `appsettings.json` and this document (s3 `9000`, console `9001`; the leftover SeaweedFS `8333` is gone), made the console port configurable, registry-qualified the MinIO image for Podman, and corrected the false claim that the blob resource injects `ConnectionStrings:blob`. Docker/Podman startup verified end to end. | — |
| 2026-08-30 | Created — Aspire AppHost orchestrating Postgres + MinIO blob storage + Seq for local dev, mirroring the `builder-catalogue` house style. No ChatHost (project not yet in tree). | — |
