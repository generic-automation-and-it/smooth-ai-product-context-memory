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
- The template `Program.cs` is a bare bootstrap (`CreateBuilder → Build → Run`) with no registered endpoints, so any un-routed request returns `404` — this is exactly what the Host integration smoke test asserts. Replace it with real composition (Serilog, OpenAPI/Scalar, health checks, `AddApplication`/`AddInfrastructure`, endpoint mapping) as features land.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-01 | Moved local ports `5080`/`7080` → `5141`/`7141`, derived from the set-bit count of the product name's ASCII binary, to avoid collisions with unrelated local containers. | — |
| 2026-05-30 | Created — minimal runnable Host (`Program.cs`, `appsettings(.Development).json`, `Properties/launchSettings.json`) with empty `Configuration/`, `Endpoints/`, `HealthChecks/`, `Workers/`. | — |
