# INFRASTRUCTURE_AGENTS.md

## TL;DR

Implements the contracts defined in Application — EF Core + PostgreSQL persistence (`Persistence/`, Apache AGE session init on the pooled data source), blob storage (`Storage/`), and the Markdown export disk sink (`Export/`).

## Non-Negotiables

- **Implements Application interfaces; never the reverse.** Concrete stores/clients here implement `IFoo` from Application. Application must not reference an Infrastructure concrete type.
- **References Application and Domain only** — never Host.
- **EF Core migrations are generated code.** Keep them under `Persistence/Migrations/`; they are marked generated via the root `.editorconfig` glob and generated migration classes should carry `[ExcludeFromCodeCoverage]`. Register the `DbContext` with a scoped lifetime.
- **No business rules.** Infrastructure adapts to the outside world (DB, HTTP, cache); domain decisions stay in Domain, orchestration in Application.

## Packages to add when implementing

`Microsoft.EntityFrameworkCore(.Relational/.Design/.Tools)`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Refit.HttpClientFactory`, `Microsoft.Extensions.Http.Resilience` — declared centrally in `Directory.Packages.props`.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-26 | `TarSnapshotArchive` verify hardened on both duplicate-name axes and on an incomplete manifest. A repeated tar member is no longer collapsed last-wins with no finding: `ReadEntries` now returns the repeated names beside the entry map and `VerifyAsync` emits one `Corruption` finding per repeat, the archive-side twin of the manifest's duplicate-entry guard (the three read paths ignore the repeats, as last-wins is the only ordering a corrupt archive is read in). A manifest with a null `entries` or `counts` (or the property absent, which deserialises identically) is now rejected as a `Corruption` finding on the manifest rather than throwing `NullReferenceException` out of `VerifyAsync`. | HLD-006 |
| 2026-09-26 | `TarSnapshotArchive` count reconciliation no longer throws on an unparseable or non-array corpus member: it emits a `Corruption` finding naming the member (matching the hash-mismatch finding) instead of letting `JsonException` escape `VerifyAsync`, so a corrupt member still returns a findings list rather than a raw stack trace from the `verify` verb. Count findings now carry the member name, as `SnapshotFinding` documents. | HLD-006 |
| 2026-09-26 | `TarSnapshotArchive` verify now reconciles every manifest corpus count (memories, versions, graph vertices/edges, ticket vertices/edges) against the archive member element counts, not just the blob/object count, so an altered manifest count on an untouched archive is caught. | HLD-006 |
| 2026-09-26 | `TarSnapshotArchive` records `DanglingReferences` and `MismatchedBodies` in the manifest and verify now reports an archive with either as non-clean, so a capture with an unresolvable reference no longer verifies clean but fails restore. `ReadCaptureAsync` now gates on the manifest format version before deserialising member layouts (matching `ReadAsync`), and `Deserialize<T>` returns a clean `InvalidDataException` instead of null-forgiving. | HLD-006 |
| 2026-09-13 | `NpgsqlMemoryGraph` implements `IMemoryGraph`. `memory_link` dropped; `memory_graph_cascade` is the one delete path. | HLD-003 |
| 2026-09-13 | Pooled `NpgsqlDataSource` with AGE physical-connection initialiser (`NoResetOnClose`). Graph objects created by SQL migration, not the EF model. | HLD-003 |
| 2026-09-13 | `FileSystemMarkdownExportSink` implements `IMarkdownExportSink` — wipe-and-rewrite behind `.context-memory-export`. Policy stays in Application. | PR #18 |
| 2026-09-09 | Persistence delivered. Entity POCOs live in `Domain/Entities/`; the empty `Persistence/{Entities,Repositories,Stores}/` skeleton folders were removed (unused). `Persistence/{Configurations,DesignTime,Extensions,Migrations}/` are populated. DbContext + migration + seed + triggers + `label_usage` view. See `Persistence/PERSISTENCE_AGENTS.md`. | [ADR-0002] |
| 2026-05-30 | Created — empty persistence + clients skeleton (`Clients/`, `Extensions/`, `Persistence/{Configurations,Entities,Migrations,Repositories,Stores,Extensions,DesignTime}/`). | — |
