# HOST_AGENTS.md

## TL;DR

ASP.NET Core composition root (Minimal API). Wires the application together and exposes endpoints — it holds no business logic.

## Non-Negotiables

- **Keep business logic out of Host.** Endpoints translate HTTP to a Mediator request and back; they contain no domain or orchestration logic. The `export` CLI is the same rule: parse argv, compose DI, dispatch `ExportStore` — no rendering.
- **One endpoint per use case** under `Endpoints/`; cross-cutting composition (DI, middleware, observability, problem-details) lives in `Configuration/`.
- **`Program` ends with `public partial class Program { }`** so integration tests can target it via `WebApplicationFactory<Program>`.
- **References Application, Domain, and Infrastructure** — it is the only project that composes all layers.

## Key Behaviors

- **CLI branch:** when the first argument is `export`, `Program.cs` does **not** build a `WebApplication`. It uses `Host.CreateApplicationBuilder` + `AddApplication` + `AddInfrastructure` and runs `ExportStore` once. No Kestrel, OpenAPI, or `DatabaseMigrationHostedService`. Missing schema fails the command; migrate is not a side effect of export.
- **Local ports are `5141` (http) and `7141` (https).** The number is derived from the product name so it is stable and collision-unlikely against other local services: the ASCII bit string of `smooth-ai-product-context-memory` is 256 bits long and contains **141 set bits**, giving `5000 + 141`. Reproduce with `python3 -c "n='smooth-ai-product-context-memory'; print(5000 + ''.join(format(ord(c),'08b') for c in n).count('1'))"`. If the product is renamed, recompute rather than keeping the old number. The previous `5080`/`7080` pair collided with unrelated local containers.
- Composition: `UseConfiguredSerilog()`, `AddServiceDefaults()`, OpenAPI, Scalar at `/scalar/v1`, ProblemDetails, `AddApplication`/`AddInfrastructure`, health checks, then endpoint mapping. Un-routed `/` still 404.
- **Serilog is the only logging pipeline.** `UseConfiguredSerilog` reads `Logging:Serilog` (not a top-level `Serilog` section) and adds an async Seq sink when `ConnectionStrings:seq` is present; `SEQ_URI` is honoured only as an override. `Aspire.Hosting.Seq` publishes `ConnectionStrings:seq` and no `SEQ_URI` — reading only the latter is why Seq received nothing.
- **`writeToProviders: true` plus `Logging.ClearProviders()`.** Serilog owns console and Seq; the OpenTelemetry logging provider is the only MEL provider left, and it owns OTLP. Leaving the default console provider in place prints every record twice — that pairing is the regression to watch for.
- **`preserveStaticLogger: true`.** The pipeline belongs to this host, not the global `Log.Logger`. Without it a second host in the same process — exactly what L2 does — silently redirects the first host's records into the second host's sinks and providers.
- **Service defaults are inlined** in `Configuration/HostApplicationBuilderExtensions.cs` rather than a shared `ServiceDefaults` project; the surface is too small to justify a library. Tracing covers ASP.NET Core, `HttpClient` and the `Npgsql` source; metrics add runtime and `Npgsql`. Sampling is `AlwaysOnSampler`, chosen deliberately rather than defaulted. The OTLP exporter registers only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, so a bare run exports nothing and an Aspire-launched one exports everything. **A published image registered as an Aspire *container* resource does not get that variable automatically — it needs `.WithOtlpExporter()`.**
- **`ConfidentialityTraceProcessor` enforces NFR-05 inside the trace pipeline**, where logging conventions cannot reach. It redacts the path from `url.full` (the object store is content-addressed, so the request path *is* a content address), replaces error status descriptions (a PostgreSQL unique violation embeds the offending value in its `DETAIL:` clause), and drops `exception.message`/`exception.stacktrace` while keeping `exception.type`. `db.statement` is held closed as defence in depth — Npgsql 10 emits no command text and instrumentation runs with `RecordException` off; do not enable either without re-checking this processor.
- **`/health` means ready to serve; `/alive` means the process responds.** Readiness requires migrations complete *and* PostgreSQL reachable (unhealthy → 503); an unreachable object store is `Degraded` and still answers 200, so a blob hiccup does not pull the service out of rotation. `MigrationReadinessState` is the latch: migrations run in a hosted service after Kestrel is listening, so without it the app answers against a schema that does not exist yet and still looks healthy.
- Migrations run in `IHostedService`, not before `app.Run()`. Awaiting migrate on the startup path deadlocks `WebApplicationFactory` (L2). L2 must keep that hosted service (`RemoveHostedServices = false`) and inject the connection string **before** DbContext is constructed (deferred `IConfiguration` lookup).
- Endpoints translate HTTP to Mediator only. **`ApiExceptionHandler` never inspects exception text** — it asks `IDbErrorMapper` (SQLSTATE, implemented in Infrastructure) and otherwise maps the Application exception type. Database-originated details are fixed strings, so no SQL, column name or value can leak.
- **One error contract:** RFC 7807 written as `application/problem+json` for every failure. `400` validation, `403` scope (`ForbiddenException`), `404` `NotFoundException`, `409` `ConflictException`, `500` with the fixed detail `"An unexpected error occurred."`. A request body that fails to deserialize — including an unknown property (a misspelled field is a caller mistake) — is also `400` `Invalid request body`, never absorbed and silently defaulted (`JsonUnmappedMemberHandling.Disallow`). The detail comes from the first `JsonException` in the exception chain, so it names the rejected field, and its `Path` is reported as a `path` problem extension — the `BadHttpRequestException` wrapper says only "Failed to read parameter", which is unactionable. The path is read from the exception structurally and never spliced out of message text.
- `GET .../blob` and `GET .../versions` take `?scope=` and `POST /query` takes `asOf`/`limit`; `PATCH /groups/{uuid}` and `GET|POST /initiatives` complete the registry surface. These read routes are proxies **and** scope boundaries — see `Features/FEATURES_AGENTS.md` LADR-003.
- Ticket routes are separate: `PUT /api/context/tickets/parent` dispatches `SetTicketParent` and returns `changed`; `parent` must be present (explicit null removes), while null/absent `expectedParent` expects absence. `POST /api/context/tickets/paths` dispatches `FindTicketPaths` and returns `TicketTraversalResult`; omitted `maxDepth` stays zero and fails validation. Ticket identity grants no group-scope consent; the provider drops hidden anchors and whole hidden paths without a revealing 403/404 lookup in Host/Application.
- Scalar/OpenAPI document every `/api/context/*` route; the L2 test asserts each path literally — `/api/context/paths` included, so a route added without updating `ExpectedRoutes` fails. Bind localhost (`5141`/`7141`); do not listen `0.0.0.0` in dev.
- OpenAPI schema reference IDs use full nested CLR type names. Feature slices all define `Request` and
  `Response`; short-name IDs collide and silently document the wrong body. This is compatibility-significant.
- **Container image** listens `http://+:5141` (required inside Docker). Non-root (`$APP_UID`). `ENTRYPOINT` is the Host binary so `export` remains `docker run … image export …`. Config contract and AppHost image path: `docs/wiki/docker.md`. Do not bake web args into the entrypoint.
- **Docker restore cache must include packages.** The API Dockerfile keeps NuGet packages in the build-stage restore layer, not a BuildKit cache mount. Exported GHA layer caches do not include cache-mount contents; a cached restore followed by source-invalidated `publish --no-restore` otherwise fails with NETSDK1064 on a fresh runner. Packages remain outside the final runtime image.

## Requirements

Approved context API capability boundary (2026-09-17): every `/api/context/*` route requires a
Bearer token. Read credentials can invoke only query/history/blob/path/ticket-path/label-list/
initiative-list/recall-feedback/never-recalled/recall-feedback/miss-rate operations (the two recall-feedback
read surfaces were added 2026-09-19 with HLD-004; `reset` needs write); write credentials can invoke
both planes. Health, OpenAPI and Scalar stay
public. Tokens come only from runtime configuration, compare in constant time, and never enter logs,
traces, committed settings or OpenAPI examples.

Approved release-image plan (2026-09-13):

1. Repo-root multi-stage Dockerfile: copy CPM props + `NuGet.Config` + Host graph csprojs, restore, copy sources, publish; runtime `aspnet:10.0-alpine`, non-root, OCI labels, `ENTRYPOINT` the Host binary.
2. `.github/workflows/publish-image.yml` publishes API and `-apphost` images only on main pushes after merge. PR CI builds/tests without uploading artifacts. One repository-wide `queue: max` group serializes same-commit tests, candidates, native smoke, and latest/SHA promotion. No tag/manual publication or GitHub Release creation; see `docs/wiki/ci.md`.
3. AppHost default compiles Host from the working tree (`HostConfiguration:UseProject=true`). Published image is opt-in (`UseProject=false`); container `mimisbrunnr-host` in Docker Desktop group `smooth-mímisbrunnr`. Keep `AddProject`. Inject `ConnectionStrings__SmoothAiProductContextMemory`. The development AppHost is not published; the release controller ships as the separate `-apphost` image (APPHOST_AGENTS.md LADR-005). Published image path needs `.WithOtlpExporter()`.
4. Run contract in `docs/wiki/docker.md`, verified by executing the documented build.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-19 | Read-capability enumeration extended with the two recall-feedback read surfaces (`never-recalled`, `miss-rate`); `reset` needs write. | HLD-004, PR #79 |
| 2026-09-17 | OpenAPI schema IDs now use full nested CLR names, preventing vertical slices' repeated `Request`/`Response` names from colliding and exposing the wrong contract. | HLD-002 wire compatibility |
| 2026-09-17 | Context routes now require distinct runtime read/write Bearer capabilities; public health/OpenAPI/Scalar remain unchanged. | HLD-002 NFR-04 |
| 2026-09-17 | Approved API-enforced read/write capability boundary for delegated context-memory agents. | HLD-002 NFR-04 |
| 2026-09-16 | Updated publication contract to main-only; PR builds/tests do not upload artifacts. | PR #65 |
| 2026-09-16 | Persisted API NuGet packages alongside restore metadata in Docker build layers so fresh runners can publish after importing GHA layer cache. | PR #65, Actions run 35014970369 |
| 2026-09-15 | Publish-model summary synced with the coordinated release pipeline: two images (API + `-apphost` controller) promoted inside one repository-wide `queue: max` group, prereleases never update stable aliases, and the release controller ships as the `-apphost` image. | ai-analyse |
| 2026-09-14 | Added ticket parent and bounded ticket path route mappings, explicit-null removal wire guard, and HTTP validation/scope/hierarchy plus OpenAPI route coverage. | HLD-003 LADR-08 |
| 2026-09-14 | `Invalid request body` reports the first `JsonException` in the exception chain as the detail and its `Path` as a `path` problem extension, not the `BadHttpRequestException` wrapper. The wrapper names no field, so a rejected batch gave the caller nothing to fix — proven by an e2e run lost to one unmapped member. | e2e-dogfood |
| 2026-09-13 | All endpoints reject unknown JSON body fields as `400` (`JsonUnmappedMemberHandling.Disallow`); `ApiExceptionHandler` maps deserialization failures (`JsonException`/`BadHttpRequestException`) to `400` `Invalid request body`. | BUG-03 |
| 2026-09-13 | AppHost default is working-tree Host; published image remains opt-in. | APPHOST_AGENTS.md |
| 2026-09-13 | Host Dockerfile + GHCR publish; image serves API and `export`; AppHost image mode still runs Host as `mimisbrunnr-host` in group `smooth-mímisbrunnr`. | release-image |
| 2026-09-13 | Delivered the observability wiring: Serilog → console/Seq forwarding to the OpenTelemetry provider for OTLP logs/traces/metrics, real `Logging:Serilog` sections, `AddServiceDefaults`, `ConfidentialityTraceProcessor`, and `/health` + `/alive` gated on migration completion. Verified from a published artifact in Production — Serilog console, Seq ingestion and all three OTLP signals. | PR #36 |
| 2026-09-13 | `export` CLI branch before `WebApplication.CreateBuilder` — generated Markdown dump, no HTTP. | PR #18 |
| 2026-09-11 | Error handler classifies via `IDbErrorMapper` instead of message text; `application/problem+json`; `403` for scope. Added `PATCH /groups/{uuid}`, `GET|POST /initiatives`, `?scope=` on the blob route. | PR #14 review |
| 2026-09-23 | Added read-only `POST /api/context/dossier/bundle` and `/dossier/preview` (HLD-005 bundle/preview API; `ApiCapability.Read`, mediate to Application, no model call). | HLD-005 |
| 2026-09-12 | `GET .../versions` now takes `?scope=` (scope-gated like the blob route); `POST /memories` OR-merges a body-supplied `dryRun` so it is not silently treated as a real write. | /ai-review PR #14 |
| 2026-09-10 | Composed Mediator API: OpenAPI/Scalar, ProblemDetails, ADR-0003 endpoints under `Endpoints/`. | PR #14 |
| 2026-09-01 | Moved local ports `5080`/`7080` → `5141`/`7141`, derived from the set-bit count of the product name's ASCII binary, to avoid collisions with unrelated local containers. | — |
| 2026-05-30 | Created — minimal runnable Host (`Program.cs`, `appsettings(.Development).json`, `Properties/launchSettings.json`) with empty `Configuration/`, `Endpoints/`, `HealthChecks/`, `Workers/`. | — |
