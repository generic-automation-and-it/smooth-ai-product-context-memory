# LADR-05: Rename `Memory`→`Understanding` in code; DB tables, HTTP API and MCP wire stay stable

**Status:** Draft

## Context

BRD-003 BR-45 requires the product to name the distilled unit "Understanding", not "memory". This is
partly a vocabulary preference, but the question is how far the rename reaches.

There are three distinct surfaces the word `Memory` touches:
1. **C# code identifiers** — the `Memory` entity, `IMemorySearch`, `IMemoryGraph`, `MemoryScopeFilter`,
   `MemoryLink`, and the ~44 files that reference them.
2. **Database schema** — tables `memory`, `memory_group`, `memory_version`, columns `memory_id`.
3. **Public contract** — HTTP routes under `/api/context`, request/response bodies, and MCP tool names
   (`memory-read`, `memory-write`).

Renaming surfaces 2 and 3 is a breaking change: it requires a destructive data migration on the live
store, and it breaks every external consumer of the API and MCP server. The user, when asked, chose
"code identifiers only, keep DB + wire".

## Decision

Rename surface 1 only, and keep surfaces 2 and 3 stable:

- C# **type** names containing `Memory` become `Understanding` (entity, search/graph interfaces, scope
  filter, links, and referencing files and tests).
- Database table and column names are unchanged (`memory`, `memory_group`, `memory_version`,
  `memory_id`), as are index/PK/FK names and the AGE graph name `memory_graph` and its labels.
- HTTP routes, request/response **field names** and MCP tool names are unchanged.

The product vocabulary change ("Understanding" for the distilled unit) is expressed in prose, the
glossary and user-facing terms; the identifier rename is a separate, code-only refinement.

### The constraint that decides the rename's true boundary

Serialization is **convention-based, not attribute-based**. The Host configures
`JsonSerializerDefaults.Web` (camelCase) and there is **not a single `JsonPropertyName` attribute in
the solution**; `JsonbConverter` does the same for jsonb columns. Therefore:

> **A C# property name is the wire contract, and for jsonb-persisted documents it is also the stored
> data contract.**

Renaming a property called `MemoryUuid` silently changes the JSON field from `memoryUuid` to
`understandingUuid` for every API consumer, and — on a jsonb document type — changes the keys inside
already-stored rows. Neither is a code-only change.

The rename is therefore bounded as:

| Surface | Rename? | Why |
|---|---|---|
| **Type names** (classes, records, interfaces) | **Yes** | A type name is never serialized; only its members are |
| **Local variables, parameters, private fields, method names** | **Yes** | Internal only |
| **Properties on HTTP request/response DTOs** | **No** | The property name *is* the JSON field (camelCased) |
| **Properties on jsonb-persisted document types** | **No** | The property name *is* the stored jsonb key |
| **EF entity property names** | **Only with** `HasColumnName` already pinning the column, **and** the EF model-snapshot property strings updated in the same change |
| **BCL `MemoryStream`, `Memory<T>`, `ReadOnlyMemory<T>`** | **No** | Unrelated framework types |
| **Any `memory*` inside a string literal** | **No** | SQL, AGE/Cypher, route templates, index/FK/PK names |

A further coupling: the EF **model snapshot and existing migrations reference entity CLR type names and
navigation/property names as string literals** (e.g. `"SmoothAiProductContextMemory.Domain.Entities.Memory"`,
`"MemoryId"`, `"SourceMemory"`). Renaming a type or an EF-mapped property therefore requires updating
those strings in the same change, or EF model validation fails — while leaving the `ToTable("memory")`
table names untouched.

**Because of the above, the rename is a standalone change, not part of the understanding-transfer
feature.** It touches ~116 files, its value is naming clarity, and its two hazards (wire and stored
jsonb) are invisible in a diff that also adds a feature. Bundling it would make the feature diff
unreviewable and put an API/data break behind a cosmetic change.

## Alternatives Considered

- **Rename everything incl. DB and wire** — rejected: destructive migration on the live store and a
  breaking change to every consumer; the user explicitly kept DB and wire stable.
- **Doc/prose only, no code rename** — rejected: code would keep the `Memory` identifier while the
  product talks about Understandings, which is the "kept a description" smell; and the user chose a
  code rename.
- **Alias types to avoid the rename** — rejected: an alias is a second name for one thing, indirection
  with no benefit over the rename itself.
- **Rename DTO properties and add `[JsonPropertyName("memoryUuid")]` to hold the wire** — rejected: it
  is a compatibility shim on every renamed member, added purely so a cosmetic rename does not break
  consumers. It also makes the wire contract depend on an attribute nobody currently has to maintain,
  so the next renamer removes it and breaks the API silently.
- **Bundle the rename into the understanding-transfer change** — rejected: see the Decision. A feature
  diff that also carries an API-and-stored-data hazard cannot be reviewed for either.

## Consequences

- No DB migration, no stored-jsonb key change and no breaking API/MCP change (NFR-04).
- The rename is a **separate change**: ~116 files reference the token, and it must move type names and
  EF model-snapshot strings together while leaving DTO/jsonb property names alone.
- The codebase will temporarily read `Memory` in code while the product says "Understanding" in prose.
  That inconsistency is accepted deliberately, because the alternative is either a shim on every wire
  member or a silent API break.
- Whoever performs the rename must verify NFR-04 afterwards: no table/column/index/FK name changed, no
  route changed, no JSON field changed, no jsonb key changed, no MCP tool name changed.

## Related

- **BRD-003 BR-45** — the naming requirement.
- **NFR-04** — wire stability, verified by unchanged route/table/MCP names.
