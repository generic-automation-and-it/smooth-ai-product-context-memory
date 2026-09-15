# CI/CD

Two workflows: the PR gate (build + test) and a separate image publish to GHCR.
The publish workflow does **not** run on pull requests.

## PR Gate

- **Workflow:** `.github/workflows/pr-gate.yml`
- **Triggers:** `pull_request` → `main` (including PR branch updates), `push` → `main`, and manual `workflow_dispatch`.
- **Paths filter:** source/tests, scripts, Dockerfiles, solution/build/package inputs, local actions, and PR/publish workflows trigger checks. Docs-only PRs skip the gate; dispatch and reusable workflow calls run explicitly.
- **Policy checks:** release event/version/promotion tests and engine-free controller preflight/lifecycle tests run with the build. PRs also build both container architectures on native runners without publication.

### Steps

1. **Checkout** — `actions/checkout@v4`.
2. **Install .NET SDK** — `actions/setup-dotnet@v4` (version from the `DOTNET_VERSION` env, currently `10.0.x`).
3. **Restore** — `dotnet restore SmoothAiProductContextMemory.slnx`.
4. **Build** — `dotnet build --no-restore --configuration Release`.
5. **Aspire test with coverage** — local action `.github/actions/aspire-test-with-coverage`:
    - Starts `tests/SmoothAiProductContextMemory.TestFramework.Aspire`, keeps its PID inside the action script, and waits for PostgreSQL (`127.0.0.1:15432`, image `docker.io/apache/age:release_PG17_1.7.0`), Redis (`127.0.0.1:16379`), WireMock (`http://127.0.0.1:19091/__admin/health`), MinIO TCP (`127.0.0.1:9002`), then MinIO HTTP (`http://127.0.0.1:9002/minio/health/live`). On MinIO timeout the action dumps `docker logs mimisbrunnr-testcontainer-blob`. MinIO image is pinned to `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z`.
   - Restores .NET tools (`dotnet tool restore`) after the dependency pre-warm, matching the proven CI timing before tests start.
   - Prepares `artifacts/testresults/` and `artifacts/coverage/`.
    - Runs test projects in order: Host integration → Application/Infrastructure component → Domain/Application/Infrastructure/Host/AppHost unit tests.
   - Generates coverage reports with `dotnet tool run reportgenerator`.
   - Stops the Aspire host from the action script's teardown trap once tests and coverage have finished or failed.
6. **Publish coverage summary** (`if: always()`) — appends `artifacts/coverage/SummaryGithub.md` to the GitHub step summary.
7. **Upload coverage artifacts** (`if: always()`) — uploads `artifacts/coverage/` as `coverage-report`.

## .NET local tools

`.config/dotnet-tools.json` declares the local tool manifest, restored in CI (and locally) with `dotnet tool restore`:

| Tool | Version | Command |
|---|---|---|
| `dotnet-reportgenerator-globaltool` | `5.4.4` | `reportgenerator` |
| `dotnet-ef` | `10.0.8` | `dotnet-ef` |

`dotnet-ef` is pinned to the EF Core runtime version (`Directory.Packages.props`) so the migrations CLI never drifts from the `Microsoft.EntityFrameworkCore.*` packages. Bump both together.

## Publish image

- **Workflow:** `.github/workflows/publish-image.yml`
- **Triggers:** `push` → `main` (`:latest` + short SHA), `v*` tags (semver), `workflow_dispatch` (supplied pre-release version, **never** `latest`). No `pull_request` trigger.
- **Permissions:** default `contents: read`; package writes only in image build/promotion jobs; contents writes only for release-tag reservation and GitHub Release metadata.
- **Platforms:** `linux/amd64,linux/arm64`.
- **Registries:** `ghcr.io/${{ github.repository }}` (API) and the same name suffixed `-apphost` (controller).
- **Gates:** reuse PR build/tests for the same commit, publish immutable candidates, then smoke their exact digests on native amd64/arm64 runners. Controller embeds the API multi-platform digest. Native runner availability must be confirmed for this repository.
- **Identity:** strict SemVer/OCI validation; no build metadata, blank versions, or numeric leading zeros. Candidates include full SHA, run ID, and attempt. Dispatch never publishes latest or major.minor; prereleases never update stable aliases.
- **Promotion:** one repository-wide concurrency group with `queue: max` (up to 100 pending runs). Fresh source refs prevent stale latest/major.minor promotion. Existing version images must match tested digests; conflicting rebuilds require a new version. API and controller alias writes are sequential, not transactional; retain candidate digests to diagnose partial promotion.
- **Releases:** validate existing Git tag target before alias mutation; dispatch reserves a missing version tag at the tested commit. Release creation uses that verified tag and updates an existing draft to published. Protect release tags against external retargeting/deletion.
- **Credentials:** smoke uses a temporary dedicated Docker config usable inside Linux controller, not runner/platform credential helpers. Config is deleted after smoke; evidence excludes raw controller logs and login tokens.
- **Timeouts/cache:** build jobs 45 minutes; smoke 20 minutes per native architecture. Caches are separated by image/ref and by architecture for PR builds.
- **Validation caveat:** older actionlint builds reject `concurrency.queue`; current GitHub Actions documentation supports it. Validate other workflow diagnostics normally, not by deleting the queue policy.
- **Local run / configuration contract:** [docker.md](./docker.md).
