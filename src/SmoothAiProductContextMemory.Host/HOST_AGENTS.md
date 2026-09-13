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
- **One error contract:** RFC 7807 written as `application/problem+json` for every failure. `400` validation, `403` scope (`ForbiddenException`), `404` `NotFoundException`, `409` `ConflictException`, `500` with the fixed detail `"An unexpected error occurred."`.
- `GET .../blob` and `GET .../versions` take `?scope=` and `POST /query` takes `asOf`/`limit`; `PATCH /groups/{uuid}` and `GET|POST /initiatives` complete the registry surface. These read routes are proxies **and** scope boundaries — see `Features/FEATURES_AGENTS.md` LADR-003.
- Scalar/OpenAPI document every `/api/context/*` route; the L2 test asserts each path literally — `/api/context/paths` included, so a route added without updating `ExpectedRoutes` fails. Bind localhost (`5141`/`7141`); do not listen `0.0.0.0` in dev.
- **Container image** listens `http://+:5141` (required inside Docker). Non-root (`$APP_UID`). `ENTRYPOINT` is the Host binary so `export` remains `docker run … image export …`. Config contract and AppHost image path: `docs/wiki/docker.md`. Do not bake web args into the entrypoint.

## Requirements

Approved release-image plan (2026-09-13):

1. Repo-root multi-stage Dockerfile: copy CPM props + `NuGet.Config` + Host graph csprojs, restore, copy sources, publish; runtime `aspnet:10.0-alpine`, non-root, OCI labels, `ENTRYPOINT` the Host binary.
2. `.github/workflows/publish-image.yml` mirrors smooth-llm-imposter: GHCR, QEMU+Buildx, metadata tags, GHA cache per workflow+ref, `linux/amd64,linux/arm64`. No `pull_request` trigger. Dispatch never tags `latest`.
3. AppHost default is the published Host image (`HostConfiguration:Image`). Container `mimisbrunnr-host` in Docker Desktop group `smooth-mímisbrunnr`. `UseProject=true` keeps `AddProject`. Inject `ConnectionStrings__SmoothAiProductContextMemory`. AppHost is not published. Published image path needs `.WithOtlpExporter()`.
4. Run contract in `docs/wiki/docker.md`, verified by executing the documented build.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-13 | Host Dockerfile + GHCR publish; image serves API and `export`; AppHost pulls Host as `mimisbrunnr-host` in group `smooth-mímisbrunnr`. | release-image |
| 2026-09-13 | Delivered the observability wiring: Serilog → console/Seq forwarding to the OpenTelemetry provider for OTLP logs/traces/metrics, real `Logging:Serilog` sections, `AddServiceDefaults`, `ConfidentialityTraceProcessor`, and `/health` + `/alive` gated on migration completion. Verified from a published artifact in Production — Serilog console, Seq ingestion and all three OTLP signals. | PR #36 |
| 2026-09-13 | `export` CLI branch before `WebApplication.CreateBuilder` — generated Markdown dump, no HTTP. | PR #18 |
| 2026-09-11 | Error handler classifies via `IDbErrorMapper` instead of message text; `application/problem+json`; `403` for scope. Added `PATCH /groups/{uuid}`, `GET|POST /initiatives`, `?scope=` on the blob route. | PR #14 review |
| 2026-09-12 | `GET .../versions` now takes `?scope=` (scope-gated like the blob route); `POST /memories` OR-merges a body-supplied `dryRun` so it is not silently treated as a real write. | /ai-review PR #14 |
| 2026-09-10 | Composed Mediator API: OpenAPI/Scalar, ProblemDetails, ADR-0003 endpoints under `Endpoints/`. | PR #14 |
| 2026-09-01 | Moved local ports `5080`/`7080` → `5141`/`7141`, derived from the set-bit count of the product name's ASCII binary, to avoid collisions with unrelated local containers. | — |
| 2026-05-30 | Created — minimal runnable Host (`Program.cs`, `appsettings(.Development).json`, `Properties/launchSettings.json`) with empty `Configuration/`, `Endpoints/`, `HealthChecks/`, `Workers/`. | — |
