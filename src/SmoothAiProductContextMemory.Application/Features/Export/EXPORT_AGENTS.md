# EXPORT_AGENTS.md

## TL;DR

One-way generated Markdown projection of the store (groups, memories, current versions, history on request). Reconstructs documents from hash-addressed blobs plus database metadata. **Generated, never maintained** — disposable, regenerable, never hand-edited, never imported back.

## Non-Negotiables

- **Generated, never maintained.** Output is a projection. Hand-editing it is a defect. There is no import path and there must not be one — HLD 001 chose database-as-truth; import would reverse that by the back door. *(The one, deliberate exception for this dump is the understanding load/import capability, which lives in HLD 007 with an opt-in `--store` path through the capture skill; it does not reopen an import path for this forensic dump.)*
- **Current-only by default.** `--history` adds version chains as extra sections in the same file. History is not the default — it duplicates content and would swamp the tree.
- **Blob bodies are inlined.** Never print a content hash as if it were the document. A missing blob warns and continues; a non-text blob is noted and omitted.
- **Relationships live in file content.** `group_uuid` is written into every memory file. Directory placement is navigation, not proof. A prior trial encoded the parent only in the path; a moved file lost it silently.
- **Forensic dump, not HTTP retrieval.** Export bypasses `MemoryScopeFilter` and does not use `IMemorySearch`. Programme rows are included and marked. LADR-003 stays HTTP-only.
- **Never log memory content.** Information = lifecycle + counts. Debug = per-entity uuid. Warning = missing/non-text blob (uuid + version, not address or body).
- **No schema change, no 7th entity, no API endpoint.** Relationships come from `IMemoryGraph`, not a `DbSet`.

## System Context

HLD 001 made PostgreSQL the source of truth and recorded the cost: the store is opaque without tooling. HLD 001 also made blobs content-addressed, so a directory of SHA-256 hashes is no more readable than a table. This command is that tooling.

```mermaid
sequenceDiagram
    participant CLI as Host CLI
    participant Handler as ExportStore
    participant Db as IApplicationDbContext
    participant Blob as IBlobStorage
    participant Sink as IMarkdownExportSink

    CLI->>Handler: Request(output, history, force)
    Handler->>Db: all groups, memories, versions
    Handler->>Graph: ListAllAsync (IMemoryGraph)
    Handler->>Sink: PrepareAsync (wipe + marker)
    loop each memory version needed
        Handler->>Blob: GetAsync (null = missing, continue)
        Handler->>Sink: WriteFileAsync(relative, markdown)
    end
    Handler-->>CLI: counts
```

## Architecture Decisions

### LADR-101: Application policy, Infrastructure sink, Host argv

- **Date**: 2026-09-13
- **Status**: Accepted
- **Context**: The worktask allowed Infrastructure or a small console entry point. Clean Architecture forbids business rules in Infrastructure; a second console csproj is a second composition root.
- **Decision**: `Features/Export/` owns tree shape, frontmatter, slugs, collision, sort, and banners. `IMarkdownExportSink` writes bytes. Host parses `export` before `WebApplication.CreateBuilder` and composes `Host.CreateApplicationBuilder` + `AddApplication` + `AddInfrastructure` (user secrets loaded explicitly — the CLI host defaults to Production) — no Kestrel, no OpenAPI, no migrate-on-start. YAML is hand-rolled (no YamlDotNet): free-text scalars are quoted with `\n`/`\r`/`\t`/control-char escapes, and numeric-looking strings are quoted to keep their YAML type `string`.
- **Consequences**: L0/L1 live under Application tests. `System.CommandLine` is Host-only.

### LADR-102: Forensic dump bypasses MemoryScopeFilter

- **Date**: 2026-09-13
- **Status**: Accepted
- **Context**: HTTP retrieval hides `program` so it cannot be cited as shipped product fact. Export answers "what does it actually know?"
- **Decision**: Read `IApplicationDbContext` DbSets directly. Include every scope and status. Mark `program` and `self` with YAML `scope:` plus a body banner.
- **Consequences**: LADR-003 is unchanged for HTTP. Export never calls `IMemorySearch`.

### LADR-103: Wipe-and-rewrite behind a marker

- **Date**: 2026-09-13
- **Status**: Accepted
- **Context**: Merging into an existing tree leaves ghost files after a store delete, so two runs of the same store would not be byte-identical.
- **Decision**: Write marker `.context-memory-export`. Wipe when the directory is empty or contains the marker. Refuse an unmarked non-empty directory unless `--force`. Refuse `/` and a directory that is a git root.
- **Consequences**: Default output `.context/mimisbrunnr-memories/` (gitignored). Tests use `--force` or a fresh temp dir.

### LADR-104: Generated, never maintained

- **Date**: 2026-09-13
- **Status**: Accepted
- **Context**: A trial's hand-written index became a maintenance obligation and drifted immediately.
- **Decision**: One-way projection. Every file carries a GENERATED marker. Output directories are gitignored. No import, no Obsidian wiki-links, no graph view.
- **Consequences**: Adding import later is a defect against HLD 001.

## Key Behaviors

- **Run:** `dotnet run --project src/SmoothAiProductContextMemory.Host -- export [--output DIR] [--history] [--force]`. Needs the same `ConnectionStrings:SmoothAiProductContextMemory` and `BlobStorage:*` as the API (Aspire AppHost or user secrets). Missing schema fails the command — export does not migrate.
- **Layout:** `groups/<group-slug>/_group.md` and `groups/<group-slug>/mem-<subject-slug>.v<currentVersion>.md`.
- **Group slug:** `Slug.Subject` of the current `GroupDescription.Name` (highest version). No/unslugable name → `group-<uuid>`. Slug clash (Uuid-sorted): first keeps the clean name; later get `--` + first 8 hex of `Uuid` (`N` format); if that still clashes, full 32-hex `Uuid` (guaranteed unique).
- **Memory file clash:** same suffix rule. In-group `subject_slug` is unique, so this is a backstop.
- **Frontmatter** is a closed key list, fixed order, UTF-8 no BOM, LF scaffolding, exactly one trailing newline on the file. No `exported_at`. Timestamps use `DateTimeOffset` round-trip `"O"`.
- **Both time axes:** `valid_from` / `valid_until` = business time; `created_on` = system time.
- **`--history`:** extra `## Version N` (and extra group-description sections) in the same file, version descending, excluding current. Default omits them.
- **Links** are listed in the memory file (outgoing and incoming): relation, reason, other uuid, subject, other `group_uuid`. No extra graph files.
- **Empty store:** marker only; no `groups/` directory; does not throw.
- **Empty group:** still emits `_group.md`.
- **Plaintext on disk.** Output inherits the store's sensitivity. `--history` may include unredacted older versions. Keep it gitignored and locally encrypted.

## Requirements

Approved implementation plan (2026-09-13):

1. `IMarkdownExportSink` in Application; `FileSystemMarkdownExportSink` in Infrastructure.
2. Pure `ExportPaths` + `ExportRenderer` (L0-tested).
3. `ExportStore` Mediator slice: `Request(OutputDirectory, IncludeHistory, Force)`.
4. Host CLI: `dotnet run --project src/SmoothAiProductContextMemory.Host -- export [--output DIR] [--history] [--force]`.
5. Gitignore `export/` and `.context/mimisbrunnr-memories/`. Root `AGENTS.md` command list updated.

## Test References

- L0: `tests/SmoothAiProductContextMemory.Application.UnitTest/Features/Export/` — paths, collision, frontmatter order, banners, history on/off, missing-blob note, encoding, gitignore exact lines.
- L1: `tests/SmoothAiProductContextMemory.Application.ComponentTest/Features/ExportStoreHandlerTests.cs` — seeded store vs real Postgres; byte-identical re-run; blob inline; missing blob warns; current-only vs `--history`; `program`/`self` banners; empty store; name clash; links; `group_uuid` in file content.

## Quality Constraints

- Determinism over performance. Stream blobs one at a time; dispose each `BlobContent`.
- Scaffolding is LF even on Windows. Blob bytes are inlined as decoded UTF-8 without rewriting their interior newlines.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-01 | **The export's case-fold safety is now pinned by a test, and the reason it is safe is recorded here rather than left implicit.** `AssignMemoryFiles` and `AssignGroupFolders` compare with `StringComparer.Ordinal`, which cannot see that `mem-foo.v1.md` and `mem-Foo.v1.md` are one file on a case-insensitive filesystem (the default on macOS and Windows) — the second write would silently replace the first, and no post-copy parity check can detect it, because by then the destination *is* the source. The path is safe **by construction, not by check**: every stored `SubjectSlug` originates at `Slug.Subject`, which lower-cases each character, and the uuid-suffix fallback then separates what genuinely collides. `ExportPathsTests` pins that at the boundary the export actually uses, with a negative control asserting the slug collapses two case-differing descriptions — so a future caller passing an unslugged value fails in the suite rather than on someone's disk. **Review round: the `used` sets compared `Ordinal` on names written straight to disk, so an unslugged
value produced two names that are one file on macOS and no collision suffix was applied.** This is the
defect the "safe by construction" reading had been resting on: `Slug.Subject` lower-cases, so in practice
the set never fired — which is exactly why the exposure survived. `MemoryInput.SubjectSlug` is an
unvalidated `string` that `ExportStore` forwards verbatim from the stored column, and an untrusted
archive restore or a direct database write can carry an unslugged value. **Both** `AssignMemoryFiles` and
`AssignGroupFolders` now use `OrdinalIgnoreCase`, so the guard holds at the boundary rather than depending
on a single producer upstream of it. Found by a new test that feeds case-differing names directly: the
memory case **fails without the fix** and is mutation-verified. The **group** case does not discriminate —
`GroupSlug` routes names through `Slug.TrySubject` first, so they fold before the set sees them and the
collision is found either way; that test is documented as a pin, not as evidence, and the group change is
defence in depth rather than a fix. Harness 11 → 13 tests.

Mutation-verified: removing the lower-casing in `Slug` fails 4 of 11 tests in the class — the four that assert a folded name. The collision tests feed already-distinct or already-lowercase literals and stay green, so they are not evidence for the slug property. **The earlier version of the new test also counted the two names under `OrdinalIgnoreCase`, which read as the case-insensitivity guard and was not — mutating it to `Ordinal` left the suite green, since the names differ by more than case once the uuid suffix applies. It was removed rather than kept as reassurance; a second assertion restating the first is a defect this repo has already paid for twice.** No production change. | ICM adoption |
| 2026-09-19 | Memory files carry the DB current version in the filename (`mem-<slug>.v<N>.md`), so on-disk reflects store versioning; default output dir `.context/export` -> `.context/mimisbrunnr-memories`. | export versioning |
| 2026-09-19 | Scoped the "no import path" guardrail to this forensic dump and noted the understanding load/import capability as a deliberate exception owned by HLD 007 (opt-in `--store` through the capture skill). | HLD-007; BRD-003 |
| 2026-09-13 | ADR-0001/0002 deleted; authority citations retargeted to HLD 001. | HLD-001 |
| 2026-09-13 | Export reads relationships via `IMemoryGraph.ListAllAsync` after the HLD 003 cutover. | HLD-003 |
| 2026-09-13 | Created — generated Markdown export contract. | PR #18 |
