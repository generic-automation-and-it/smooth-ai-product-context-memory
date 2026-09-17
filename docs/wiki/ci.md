# CI/CD

Two workflows: the PR gate (build + test) and a separate image publish to GHCR.
The publish workflow does **not** run on pull requests.

## PR Gate

- **Workflow:** `.github/workflows/pr-gate.yml`
- **Triggers:** `pull_request` → `main` (including PR branch updates), `push` → `main`, and manual `workflow_dispatch`.
- **Paths filter:** source/tests, scripts, the owned context-memory skill and its two project-agent registrations, Dockerfiles, solution/build/package inputs, local actions, and PR/publish workflows trigger checks. Docs-only PRs skip the gate; dispatch and reusable workflow calls run explicitly.
- **Policy checks:** main-only release event/promotion tests and engine-free controller preflight/lifecycle tests run with the build. PRs also build both container architectures on native runners without publication. PR/manual CI keeps logs, summaries, and caches, but uploads neither Docker build records nor coverage artifacts. Images are build-only; full packaged-controller smoke runs in the main publication pipeline.

### Steps

1. **Checkout** — `actions/checkout@v4`.
2. **Test release policy** - `python3 -B -m unittest discover -s scripts -p 'test_release_policy.py' -v`; fails closed before installing the SDK.
3. **Test context-memory skill** - `python3 -B .agents/skills/mimisbrunnr-context-memory/tests/run_tests.py`; deterministic plumbing, agent grants, deep-search bounds and divergence composition only.
4. **Install .NET SDK** — `actions/setup-dotnet@v4` (version from the `DOTNET_VERSION` env, currently `10.0.x`).
5. **Restore** — `dotnet restore`.
6. **Build** — `dotnet build --no-restore --configuration Release`.
7. **Test controller preflight and lifecycle** - `python3 scripts/test-apphost-entrypoint.py`; engine-free tests against the built AppHost output.
8. **Aspire test with coverage** — local action `.github/actions/aspire-test-with-coverage`:
    - Starts `tests/SmoothAiProductContextMemory.TestFramework.Aspire`, keeps its PID inside the action script, and waits for PostgreSQL (`127.0.0.1:15432`, image `docker.io/apache/age:release_PG17_1.7.0`), Redis (`127.0.0.1:16379`), WireMock (`http://127.0.0.1:19091/__admin/health`), MinIO TCP (`127.0.0.1:9002`), then MinIO HTTP (`http://127.0.0.1:9002/minio/health/live`). On MinIO timeout the action dumps `docker logs mimisbrunnr-testcontainer-blob`. MinIO image is pinned to `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z`.
   - Restores .NET tools (`dotnet tool restore`) after the dependency pre-warm, matching the proven CI timing before tests start.
   - Prepares `artifacts/testresults/` and `artifacts/coverage/`.
    - Runs test projects in order: Host integration → Application/Infrastructure component → Domain/Application/Infrastructure/Host/AppHost unit tests.
   - Generates coverage reports with `dotnet tool run reportgenerator`.
   - Stops the Aspire host from the action script's teardown trap once tests and coverage have finished or failed.
9. **Publish coverage summary** (`if: always()`) — appends `artifacts/coverage/SummaryGithub.md` to the GitHub step summary.
10. **Upload coverage artifacts** — uploads `artifacts/coverage/` as `coverage-report` only for main pushes (including failed main runs). PR and manual CI skip upload.

## .NET local tools

`.config/dotnet-tools.json` declares the local tool manifest, restored in CI (and locally) with `dotnet tool restore`:

| Tool | Version | Command |
|---|---|---|
| `dotnet-reportgenerator-globaltool` | `5.4.4` | `reportgenerator` |
| `dotnet-ef` | `10.0.8` | `dotnet-ef` |

`dotnet-ef` is pinned to the EF Core runtime version (`Directory.Packages.props`) so the migrations CLI never drifts from the `Microsoft.EntityFrameworkCore.*` packages. Bump both together.

## Publish image

- **Workflow:** `.github/workflows/publish-image.yml`
- **Triggers:** only `push` to `main`, normally produced by merging a PR. Tag pushes and manual dispatch cannot publish. Enforce PR-only changes to main with branch protection; the trigger also covers an allowed direct main push.
- **Permissions:** default `contents: read`; package writes only in image build/promotion jobs. No Git tag or GitHub Release creation jobs or contents-write permission.
- **Platforms:** `linux/amd64,linux/arm64`.
- **Registries:** `ghcr.io/${{ github.repository }}` (API) and the same name suffixed `-apphost` (controller).
- **Gates:** reuse PR build/tests for the same commit, publish immutable candidates, then smoke their exact digests on native amd64/arm64 runners. Controller embeds the API multi-platform digest. Native runner availability must be confirmed for this repository.
- **Identity:** candidates include full SHA, run ID, and attempt. Controller version is `main-<short-sha>`; successful promotion adds `latest` and `sha-<short-sha>` to both images. No SemVer release/tag mechanism is configured.
- **Promotion:** one repository-wide concurrency group with `queue: max` (up to 100 pending runs). Fresh main ref prevents stale `latest` promotion. API and controller alias writes are sequential, not transactional; retain candidate digests to diagnose partial promotion. Use digests when immutable deployment identity is required.
- **Credentials:** smoke uses a temporary dedicated Docker config usable inside Linux controller, not runner/platform credential helpers. Config is deleted after smoke; evidence excludes raw controller logs and login tokens.
- **Alias validation:** promotion requires a nonempty JSON array of valid image tags before either registry is mutated; malformed policy output fails the job rather than producing a successful no-op.
- **Timeouts/cache:** build jobs 45 minutes; smoke 20 minutes per native architecture. Caches are separated by image/ref and by architecture for PR builds.
- **Validation caveat:** older actionlint builds reject `concurrency.queue`; current GitHub Actions documentation supports it. Validate other workflow diagnostics normally, not by deleting the queue policy.
- **Local run / configuration contract:** [docker.md](./docker.md).
