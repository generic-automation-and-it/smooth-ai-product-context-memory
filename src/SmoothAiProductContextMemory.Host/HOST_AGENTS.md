# HOST_AGENTS.md

## TL;DR

ASP.NET Core composition root (Minimal API). Wires the application together and exposes endpoints — it holds no business logic.

## Non-Negotiables

- **Keep business logic out of Host.** Endpoints translate HTTP to a Mediator request and back; they contain no domain or orchestration logic.
- **One endpoint per use case** under `Endpoints/`; cross-cutting composition (DI, middleware, observability, problem-details) lives in `Configuration/`.
- **`Program` ends with `public partial class Program { }`** so integration tests can target it via `WebApplicationFactory<Program>`.
- **References Application, Domain, and Infrastructure** — it is the only project that composes all layers.

## Key Behaviors

- **Local ports are `5141` (http) and `7141` (https).** The number is derived from the product name so it is stable and collision-unlikely against other local services: the ASCII bit string of `smooth-ai-product-context-memory` is 256 bits long and contains **141 set bits**, giving `5000 + 141`. Reproduce with `python3 -c "n='smooth-ai-product-context-memory'; print(5000 + ''.join(format(ord(c),'08b') for c in n).count('1'))"`. If the product is renamed, recompute rather than keeping the old number. The previous `5080`/`7080` pair collided with unrelated local containers.
- Composition: Serilog, OpenAPI, Scalar at `/scalar/v1`, ProblemDetails, `AddApplication`/`AddInfrastructure`, then endpoint mapping. Un-routed `/` still 404.
- Migrations run in `IHostedService`, not before `app.Run()`. Awaiting migrate on the startup path deadlocks `WebApplicationFactory` (L2). L2 must keep that hosted service (`RemoveHostedServices = false`) and inject the connection string **before** DbContext is constructed (deferred `IConfiguration` lookup).
- Endpoints translate HTTP to Mediator only. **`ApiExceptionHandler` never inspects exception text** — it asks `IDbErrorMapper` (SQLSTATE, implemented in Infrastructure) and otherwise maps the Application exception type. Database-originated details are fixed strings, so no SQL, column name or value can leak.
- **One error contract:** RFC 7807 written as `application/problem+json` for every failure. `400` validation, `403` scope (`ForbiddenException`), `404` `NotFoundException`, `409` `ConflictException`, `500` with the fixed detail `"An unexpected error occurred."`.
- `GET .../blob` and `GET .../versions` take `?scope=` and `POST /query` takes `asOf`/`limit`; `PATCH /groups/{uuid}` and `GET|POST /initiatives` complete the registry surface. These read routes are proxies **and** scope boundaries — see `Features/FEATURES_AGENTS.md` LADR-003.
- Scalar/OpenAPI document every `/api/context/*` route; the L2 test asserts each path literally. Bind localhost (`5141`/`7141`); do not listen `0.0.0.0` in dev.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-11 | Error handler classifies via `IDbErrorMapper` instead of message text; `application/problem+json`; `403` for scope. Added `PATCH /groups/{uuid}`, `GET|POST /initiatives`, `?scope=` on the blob route. | WT-2 review |
| 2026-09-12 | `GET .../versions` now takes `?scope=` (scope-gated like the blob route); `POST /memories` OR-merges a body-supplied `dryRun` so it is not silently treated as a real write. | /ai-review PR #14 |
| 2026-09-10 | Composed Mediator API: OpenAPI/Scalar, ProblemDetails, ADR-0003 endpoints under `Endpoints/`. | WT-2 |
| 2026-09-01 | Moved local ports `5080`/`7080` → `5141`/`7141`, derived from the set-bit count of the product name's ASCII binary, to avoid collisions with unrelated local containers. | — |
| 2026-05-30 | Created — minimal runnable Host (`Program.cs`, `appsettings(.Development).json`, `Properties/launchSettings.json`) with empty `Configuration/`, `Endpoints/`, `HealthChecks/`, `Workers/`. | — |
