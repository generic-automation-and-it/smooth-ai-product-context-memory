# APPLICATION_AGENTS.md

## TL;DR

Vertical-slice use cases dispatched via the Mediator source generator — one folder per feature under `Features/`, never a horizontal `Commands/`/`Queries/` split.

## Non-Negotiables

- **No `Commands/` or `Queries/` folders.** Each use case lives in `Features/<FeatureName>/` with its request, response, validator, and handler colocated.
- **No domain logic here.** Application orchestrates Domain and infrastructure abstractions; business rules live in Domain.
- **Define infrastructure contracts as interfaces here.** Persistence store contracts go in `Common/Persistence/`, external client contracts in `Common/Clients/`. Application depends on `IFoo`, never on an Infrastructure concrete type.
- **Use `Mediator` (martinothamar), not `MediatR`.** Register handlers as `Scoped` (`MediatorOptions.ServiceLifetime = ServiceLifetime.Scoped`) so they can consume scoped collaborators such as `IApplicationDbContext`; the generator defaults to Singleton, which fails DI scope validation.
- **FluentValidation runs fail-fast** in a Mediator pipeline behavior under `Common/Pipelines/`, before the handler.
- **References Domain only** — never Infrastructure or Host.
- **Persistence contract is `IApplicationDbContext`**, not the Infrastructure DbContext. Seven `DbSet`s only; never map `label_usage` as an entity.
- **Anything that needs a provider-specific operator goes behind an abstraction in `Abstractions/`, not into a handler.** `IMemorySearch` (index-matching retrieval: `to_tsvector`, `@>`) and `IDbErrorMapper` (SQLSTATE classification) are implemented in Infrastructure. A handler that reaches for provider syntax ends up filtering in memory or matching on message substrings — both were real defects here.
- **Markdown export is an Application slice, not an HTTP endpoint.** `Features/Export/` owns tree shape, frontmatter and banners; `IMarkdownExportSink` writes bytes. Contract: `Features/Export/EXPORT_AGENTS.md`. Do not add an import path.

## Slice shape (`Features/<Name>/<UseCase>.cs`)

```text
Features/
  <FeatureName>/
    <UseCase>.cs
      ├ Request    : IRequest<Response>
      ├ Response
      ├ Validator  : AbstractValidator<Request>
      └ Handler    : IRequestHandler<Request, Response>
```

Feature-level contract (uuid wire, dry-run, scope, D42): `Features/FEATURES_AGENTS.md`.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-13 | Markdown export slice (`Features/Export/`) — generated-never-maintained projection of the store. | WT-4 |
| 2026-09-11 | Added `IMemorySearch` and `IDbErrorMapper` abstractions so retrieval predicates and error classification stay provider-side. | WT-2 review |
| 2026-09-10 | HTTP API slices landed. Mediator Scoped + FluentValidation pipeline + `IApplicationDbContext`. Feature contract in `Features/FEATURES_AGENTS.md`. | WT-2 |
| 2026-05-30 | Created — empty vertical-slice skeleton (`Features/`, `Common/{Clients,Exceptions,Models,Persistence,Pipelines}/`, `Extensions/`). | — |
