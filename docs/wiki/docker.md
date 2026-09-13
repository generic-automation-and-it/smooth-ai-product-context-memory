# Host container image

Reproducible **Host** image (`src/SmoothAiProductContextMemory.Host`). The
**AppHost is not published** — it is the local Aspire orchestrator. Starting
`dotnet run --project src/SmoothAiProductContextMemory.AppHost` compiles Host
from the working tree and starts postgres/blob/seq in the `smooth-mímisbrunnr`
Docker Desktop group.

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

## Publish workflow

`.github/workflows/publish-image.yml` — not on pull requests.

| Trigger | Tags |
|---|---|
| push to `main` | `latest`, short SHA |
| `v*` git tag | semver `{{version}}` and `{{major}}.{{minor}}`, short SHA |
| `workflow_dispatch` | the supplied pre-release version (e.g. `1.0.0-rc.1`), short SHA — **never** `latest` |

Multi-arch: `linux/amd64,linux/arm64`. Confirm both in the GHCR manifest list
after the first publish (`docker buildx imagetools inspect ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory:latest`).
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
