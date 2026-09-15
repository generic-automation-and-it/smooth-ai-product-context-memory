# Container Images

Reproducible **Host** image (`src/SmoothAiProductContextMemory.Host`). The
**AppHost has a separate release-controller Dockerfile and delivery pipeline**; its full platform acceptance is still pending. Development startup with
`dotnet run --project src/SmoothAiProductContextMemory.AppHost` compiles Host
from the working tree and starts postgres/blob/seq in the `smooth-mímisbrunnr`
Docker Desktop group.

## Release Controller

The additional image is `ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory-apphost`.
It packages live Aspire 13.5.3 AppHost, DCP, dashboard, .NET runtime, and Docker client. It starts sibling containers through the external engine, not Docker-in-Docker. No host .NET installation or source checkout is required for a published controller image.

**Security:** engine socket access grants administrative control with the engine user's privileges, potentially host root. A read-only socket mount does not make API calls read-only. Never expose an unauthenticated engine API. API and Seq are not internet-ready authenticated services; use private interfaces and a protected network.

### Docker Desktop Example

Supply `PostgresConfiguration__Password`, `BlobConfiguration__AccessKey`, and `BlobConfiguration__SecretKey` in a private `controller.env` file. Never commit it. Keep credentials stable across restarts and upgrades. Replace `VERSION` with an actually published version; this documentation does not imply an image has already been released.

```bash
docker run -d --name mimisbrunnr-default-controller \
  --stop-timeout 180 \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v mimisbrunnr-default-controller-state:/var/lib/mimisbrunnr \
  --env-file controller.env \
  -e EngineConfiguration__BindAddress=127.0.0.1 \
  -p 127.0.0.1:15278:15278 \
  -p 127.0.0.1:19075:19075 \
  ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory-apphost:VERSION
```

Dashboard: `http://localhost:15278`, using the login URL printed by the controller. API: `http://localhost:5141`. The controller starts PostgreSQL 5432, MinIO 9000/9001, and Seq 5341. Those workload ports bind to the explicitly supplied address, not via `-p` on the controller. Change conflicting ports through `HostConfiguration__Port`, `PostgresConfiguration__Port`, `BlobConfiguration__Port`, `BlobConfiguration__ConsolePort`, and `SeqConfiguration__Port`.

Use `InstallationConfiguration__Id` for another installation and name its controller `mimisbrunnr-<id>-controller`. Preserve the engine-generated hostname. Container-name uniqueness prevents a second controller taking over a live installation; maintenance commands must run in a replacement canonical container, never via `docker exec` on the live controller.

### Linux, Podman, and TCP

Linux Docker bridge deployments need an explicit engine-side bind IP reachable from containers, such as the bridge gateway, plus `--add-host host.docker.internal:host-gateway` on the controller. Docker Desktop host bindings and VM bridge bindings are different: do not copy a VM gateway into a Desktop host `-p` binding. Wildcard workload binds are rejected. If publishing OTLP on another port, set `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL` to the same port inside the controller so injected telemetry addresses match.

Podman uses its Docker-compatible API through a mounted socket; set `EngineConfiguration__Kind=podman`, with `EngineConfiguration__HostAddress=host.containers.internal` unless the environment needs another reachable address. Rootless Linux commonly exposes `$XDG_RUNTIME_DIR/podman/podman.sock`; Podman Machine requires VM-aware socket forwarding/mounts. SELinux may need an explicit label exception for the socket. These paths require validation for the actual platform, not just a hostname substitution.

For secured TCP, supply `DOCKER_HOST=tcp://ENGINE:PORT`, `DOCKER_TLS_VERIFY=1`, and mounted client/CA certificates under `DOCKER_CERT_PATH`. The engine must require mutual TLS. Engine transport and workload/dashboard reachability are independent; remote engines need explicit routing for both.

Do not mount macOS `~/.docker` blindly: `credsStore: desktop` requires a helper absent from the Linux image. Public images can pull without that mount. For private GHCR packages, supply a dedicated Linux-compatible Docker config containing registry authentication, mounted read-only at `/root/.docker`. The smoke script accepts that directory through `ENGINE_CONFIG_DIRECTORY`; it no longer mounts host credentials implicitly.

### Stop, Recovery, and Upgrade

```bash
docker stop --timeout 180 mimisbrunnr-default-controller
docker rm mimisbrunnr-default-controller
```

Graceful stop removes owned workloads and preserves data volumes. Restart the same version with the same installation ID, secrets, and state volume. After forced termination, the next startup checks ownership of all resources, then replaces owned workloads; foreign resources are never adopted. Controller state and dependency data volumes are distinct.

After removing the stopped controller, `stop` and `reset` can run as commands of a replacement container with the same canonical name, socket, and installation ID. `stop` preserves all data. **`reset` permanently removes the installation's three dependency data volumes.** It does not remove the separately mounted controller-state volume. Never use dev teardown scripts against a release installation.

Back up PostgreSQL/AGE and blob storage together before upgrading. Start the new controller version with the same volumes and credentials; it uses its embedded API digest. Do not change PostgreSQL major as part of this operation. Rolling back an image does not roll back schema or data: restore a compatible cross-store backup if migrations prevent downgrade. See the graph NFR-03 restore and NFR-04 version-pairing documents.

### Verification Status

Docker Desktop 4.90.0 / Engine 29.7.2, Linux ARM64 image: API/blob/graph round-trip, graceful restart, forced recovery, scoped reset, foreign-resource rejection, and dashboard HTTP reachability passed locally. The API was an existing registry digest; this was not a historical cross-version upgrade test. Rootless Podman, Podman Machine, secured TCP, native Linux amd64/arm64 runtime, dashboard resource commands, and all telemetry panes remain unverified. The CD workflow gates promotion on native Linux smoke, but has not been executed from this workspace. Do not advertise full platform acceptance until those checks complete.

`HostConfiguration:UseProject=false` pulls this image instead (no SDK required
on the Host itself). The tag may lag the working tree.

**Base pairing:** `mcr.microsoft.com/dotnet/sdk:10.0-alpine` (build) and
`mcr.microsoft.com/dotnet/aspnet:10.0-alpine` (runtime). Bump both together.

**Published image:** `ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory`

## Configuration contract

The container dies on boot without these. Values are runtime env vars — never
build args, never baked into layers.

| Variable | Purpose |
|---|---|
| `ConnectionStrings__SmoothAiProductContextMemory` | PostgreSQL+AGE connection string. Migrations run at API startup; a missing database is a failed start, not a healthy empty process. |
| `BlobStorage__Endpoint` | S3-compatible endpoint (MinIO in the AppHost). |
| `BlobStorage__AccessKey` | Object-store access key. |
| `BlobStorage__SecretKey` | Object-store secret key. |
| `BlobStorage__Bucket` | Bucket name. Created lazily on first write. |

Optional: `ASPNETCORE_URLS` (image default `http://+:5141`), Seq via
`ConnectionStrings__seq` when the AppHost injects it.

`export` needs the same variables. It does **not** migrate; missing schema fails
the command. Default output `.context/export` is not writable in the image —
mount a host directory and pass `--output`.

## Docker Desktop group

Brand is **Mímisbrunnr**. The Docker Desktop **group label** keeps the accent
(`smooth-mímisbrunnr`). Container names are ASCII (`mimisbrunnr-*`) — Docker
rejects `í`. Tests stay `mimisbrunnr-testcontainer-*`. Image/product stays
`smooth-ai-product-context-memory`.

Standalone `docker run` and the AppHost Host container both apply:

- container name `mimisbrunnr-host`
- label `com.docker.compose.project=smooth-mímisbrunnr`
- label `com.docker.compose.service=mimisbrunnr-host`

Siblings: `mimisbrunnr-postgres`, `mimisbrunnr-blob-well`, `mimisbrunnr-seq`.

## Build locally

From the repository root (Apple silicon produces `linux/arm64`; CI publishes a
multi-arch manifest for `linux/amd64` and `linux/arm64`):

```bash
docker build -t smooth-ai-product-context-memory:local \
  --build-arg REVISION="$(git rev-parse HEAD)" .
```

Recorded local image size (linux/arm64, 2026-09-13): **57.0 MiB**
(`59763027` bytes, `smooth-ai-product-context-memory:local`).

## Run standalone (API)

Dependencies must already be reachable (AppHost postgres + blob, or equivalents).
Replace the connection string and blob endpoint with values that resolve **from
inside the container** (`host.docker.internal` on Docker Desktop).

```bash
docker run --rm \
  --name mimisbrunnr-host \
  --label com.docker.compose.project=smooth-mímisbrunnr \
  --label com.docker.compose.service=mimisbrunnr-host \
  -p 5141:5141 \
  -e ConnectionStrings__SmoothAiProductContextMemory='Host=host.docker.internal;Port=5432;Database=app;Username=postgres;Password=LocalMachineAccessNoInterestingDataDev#Passw0rd!FirewallNotExposed' \
  -e BlobStorage__Endpoint='http://host.docker.internal:9000' \
  -e BlobStorage__AccessKey='smooth-local' \
  -e BlobStorage__SecretKey='LocalMachineAccessNoInterestingDataDev#Passw0rd!FirewallNotExposed' \
  -e BlobStorage__Bucket='smooth-mimisbrunnr-memory-well' \
  smooth-ai-product-context-memory:local
```

Probe: `http://localhost:5141/openapi/v1.json` (there is no in-image healthcheck).
"Container running" is not "service working" — migrations need a reachable database.

## Run standalone (`export`)

```bash
docker run --rm \
  --name mimisbrunnr-host \
  --label com.docker.compose.project=smooth-mímisbrunnr \
  --label com.docker.compose.service=mimisbrunnr-host \
  -v "$(pwd)/.context/export:/export" \
  -e ConnectionStrings__SmoothAiProductContextMemory='Host=host.docker.internal;Port=5432;Database=app;Username=postgres;Password=LocalMachineAccessNoInterestingDataDev#Passw0rd!FirewallNotExposed' \
  -e BlobStorage__Endpoint='http://host.docker.internal:9000' \
  -e BlobStorage__AccessKey='smooth-local' \
  -e BlobStorage__SecretKey='LocalMachineAccessNoInterestingDataDev#Passw0rd!FirewallNotExposed' \
  -e BlobStorage__Bucket='smooth-mimisbrunnr-memory-well' \
  smooth-ai-product-context-memory:local \
  export --output /export
```

Arguments after the image name reach `Program` (`args[0] == "export"`). Do not
replace `ENTRYPOINT` with a baked `dotnet …` web command.

## AppHost consumption

Default: AppHost **compiles Host from the working tree** and starts
postgres/blob/seq in group `smooth-mímisbrunnr`. Startup prints
`Host mode: working tree (source).`; the dashboard resource is `host-working-tree`.

```bash
dotnet run --project src/SmoothAiProductContextMemory.AppHost
```

Published GHCR image (tag may lag the working tree; no SDK required for Host).
Startup prints `Host mode: published image <image>.`; the dashboard resource is `host-published-image`.

```bash
HostConfiguration__UseProject=false \
  dotnet run --project src/SmoothAiProductContextMemory.AppHost
```

Local image instead of GHCR:

```bash
HostConfiguration__UseProject=false \
HostConfiguration__Image=smooth-ai-product-context-memory:local \
  dotnet run --project src/SmoothAiProductContextMemory.AppHost
```

The container path injects `ConnectionStrings__SmoothAiProductContextMemory`
(the key `AddInfrastructure` reads). That override is container-only.

## Stop and reset the AppHost stack

`ContainerLifetime.Persistent` on postgres/blob/seq is deliberate: captured
memories and blobs survive an AppHost exit. The dashboard is in-process and
dies with the process, so leftover containers have no UI. That is not a leak.

Exit the AppHost first. Then pick **one** of these — they are distinct
binaries, not one command plus a flag:

```bash
scripts/stop-dev-stack.sh    # remove mimisbrunnr-{postgres,blob-well,seq,host}; keep named volumes
scripts/reset-dev-stack.sh   # same, then destroy mimisbrunnr-{postgres,blob-well,seq}-data
```

`reset-dev-stack.sh` has no prompt. Choosing it **is** the explicit ask — it
deletes the captured corpus.

Do **not** glob `mimisbrunnr-*`. That prefix also matches
`mimisbrunnr-testcontainer-*` (TestFramework.Aspire). The scripts use an exact
allowlist plus label `com.docker.compose.project=smooth-mímisbrunnr`. Missing
`mimisbrunnr-host` is success (`UseProject=true` has no Host container). Empty
stack is exit 0.

Runtime is `$DOTNET_ASPIRE_CONTAINER_RUNTIME` (default `docker`). Scripts do
not kill AppHost, DCP, or `dotnet`.

AGE-pin recreate (Persistent keeps the previous image until the container is
removed) is the same mechanism — `stop-dev-stack.sh` is the supported remove
for the dev container; see `APPHOST_AGENTS.md`.

## Publish workflow

`.github/workflows/publish-image.yml` — not on pull requests.

| Trigger | Tags |
|---|---|
| push to `main` | `latest`, short SHA |
| stable `v*` git tag | full version and major.minor (unless a newer patch reserves that lane), short SHA |
| prerelease `v*` git tag | full prerelease version and short SHA; no stable alias |
| `workflow_dispatch` | the supplied pre-release version (e.g. `1.0.0-rc.1`), short SHA — **never** `latest` |

Multi-arch: `linux/amd64,linux/arm64`. Confirm both in the GHCR manifest list
after the first publish (`docker buildx imagetools inspect ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory:latest`).

Both API and controller candidates pass same-commit tests and native-architecture smoke before aliases are promoted. The controller embeds the API's multi-platform digest. Version aliases cannot replace an existing different digest; use a new version for a rebuilt release. Promotion is serialized, and release tags must point at the tested commit. GitHub Releases link exact digests and these installation instructions. Package visibility must be configured/verified separately; private packages require registry authentication.

An amd64-only push fails on Apple silicon with a manifest error that reads like
a configuration problem.

## Verification (2026-09-13)

Executed, not inferred:

| Check | Result |
|---|---|
| `docker build -t smooth-ai-product-context-memory:local --build-arg REVISION=$(git rev-parse HEAD) .` | succeeds |
| Image size / arch | 57.0 MiB, `linux/arm64` |
| Runtime user | `uid=1654(app)` (`$APP_UID`) |
| `ENTRYPOINT` | `["./SmoothAiProductContextMemory.Host"]` |
| OCI labels | `source`, `revision`, `licenses=MIT`, `title`, `description` |
| `docker run … image export --help` | prints System.CommandLine help (args reach `Program`) |
| API against AppHost postgres+blob | `GET /openapi/v1.json` → **200**, OpenAPI 3.1.1, 11 paths; listening `http://[::]:5141` |
| `export --output /export` (empty store) | writes marker `.context-memory-export`; 0 files |
| Docker Desktop labels | `com.docker.compose.project=smooth-mímisbrunnr`, `service=mimisbrunnr-host` (verified under previous names; rename is the same label mechanism) |
| Multi-arch manifest | **not** verified locally — CI `build-push-action` platforms `linux/amd64,linux/arm64`; inspect GHCR after first publish |
| Dispatch never `:latest` | encoded in workflow `enable=` on the `latest` tag; confirm on first `workflow_dispatch` |

Alpine runtime logs `Cannot load library libgssapi_krb5.so.2` from Npgsql GSSAPI probe. Harmless; do not add kerberos packages to silence it.
