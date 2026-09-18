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
- **Persistence contract is `IApplicationDbContext`**, not the Infrastructure DbContext. Six `DbSet`s only; never map `label_usage` as an entity. Memory relationships use `IMemoryGraph`, tickets use separate `ITicketGraph` for lock/mutation/traversal; no new EF entity.
- **Anything that needs a provider-specific operator goes behind an abstraction in `Abstractions/`, not into a handler.** `IMemorySearch` (index-matching retrieval: `to_tsvector`, `@>`), `IDbErrorMapper` (SQLSTATE classification), `IMemoryGraph` (AGE Cypher) and `IMemoryTraversal` (bounded paths composed with the relational read in one statement) are implemented in Infrastructure. A handler that reaches for provider syntax ends up filtering in memory or matching on message substrings — both were real defects here.
- **Markdown export is an Application slice, not an HTTP endpoint.** `Features/Export/` owns tree shape, frontmatter and banners; `IMarkdownExportSink` writes bytes. Contract: `Features/Export/EXPORT_AGENTS.md`. Do not add an import path.
- **Ticket hierarchy does not reinterpret memory provenance.** [HLD-003 LADR-08](../../docs/hlds/003-graph-edges-on-age/ladrs/LADR-08-captured-ticket-hierarchy.md) superseded LADR-02 before migration. `Tickets/SetTicketParent` and `Tickets/FindTicketPaths` dispatch through `ITicketGraph`; group handlers acquire its shared transaction lock before ownership changes. Exact provider/key and JSONB association remain; no memory-link projection or ticket-derived scope consent. Final full-suite and benchmark evidence close the implementation release gates. Feature contract: `Features/FEATURES_AGENTS.md`.

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
| 2026-09-17 | SetMemories now resolves caller-selected create UUIDs before graph planning, allowing transactional links to new memories; cheap reads include source provenance and capture time. | HLD-002 LADR-05, NFR-04 |
| 2026-09-17 | SetMemories accepts ordered repeated version targets, retaining authority-losing claims as history while leaving selected winner current in one transaction. | HLD-002 LADR-01/LADR-04 |
| 2026-09-15 | Finalized ticket slice acceptance against the clean full-suite run and explicit performance evidence; no contract, threshold or scope relaxation. | HLD-003 LADR-08; final NFR-02 evidence |
| 2026-09-14 | Synced working-tree ITicketGraph lock/mutation/traversal abstraction and ticket slices; group handlers share the hierarchy transaction lock. Design acceptance remains distinct from implementation release acceptance while NFR-02 is open. | HLD-003 LADR-08 |
| 2026-09-14 | Distinguished accepted ticket hierarchy/traversal from delivered memory graph abstractions. Exact identity and JSONB association stay; no EF entity, memory-link projection or ticket-derived scope consent. Documentation only, implementation pending. | HLD-003 LADR-08; HLD-002 LADR-08 |
| 2026-09-13 | Added `IMemoryTraversal` — bounded multi-hop paths whose descriptive fields come from the relational rows in the *same* statement. `MemoryPathQuery.MaxDepth` is `required`: an unbounded traversal does not compile. | HLD-003 LADR-07 |
| 2026-09-13 | Relationships via `IMemoryGraph`; six `DbSet`s. CreateLink / SetMemories / Export no longer touch `memory_link`. | HLD-003 |
| 2026-09-13 | Markdown export slice (`Features/Export/`) — generated-never-maintained projection of the store. | PR #18 |
| 2026-09-11 | Added `IMemorySearch` and `IDbErrorMapper` abstractions so retrieval predicates and error classification stay provider-side. | PR #14 review |
| 2026-09-10 | HTTP API slices landed. Mediator Scoped + FluentValidation pipeline + `IApplicationDbContext`. Feature contract in `Features/FEATURES_AGENTS.md`. | PR #14 |
| 2026-05-30 | Created — empty vertical-slice skeleton (`Features/`, `Common/{Clients,Exceptions,Models,Persistence,Pipelines}/`, `Extensions/`). | — |
