# NFR-04: Compatibility — DB, HTTP API and MCP wire unchanged by the rename

**Status:** Draft

## Requirement

The `Memory`→`Understanding` rename (LADR-05) touches C# **type** names and internal identifiers only.
It does **not** change the public contract:

- Database table, column, index, PK/FK names are unchanged (`memory`, `memory_group`,
  `memory_version`, `memory_id`, `IX_memory_*`, `PK_memory*`, `FK_memory_*`), as is the AGE graph name
  `memory_graph` and its vertex/edge labels.
- HTTP routes under `/api/context` and every request/response **JSON field name** are unchanged.
- Every **jsonb key** inside stored documents is unchanged.
- MCP tool names (`memory-read`, `memory-write`) and their parameters are unchanged.
- No destructive data migration is introduced by the rename.

**Why this NFR needs teeth.** Serialization is convention-based: the Host uses
`JsonSerializerDefaults.Web` and the solution contains **no `JsonPropertyName` attributes**, and
`JsonbConverter` uses the same convention for jsonb. A C# property name is therefore simultaneously the
JSON field name and, for document types, the stored jsonb key. A property rename is a wire break and a
data break that the compiler cannot see.

## Verification

- **Contract snapshot (the load-bearing check)** — capture the OpenAPI document before the rename and
  diff it after. Zero differences in paths, schema property names and required lists. A renamed JSON
  field shows up here and nowhere else.
- **L1** — assert the store tables, columns, indexes and constraint names are unchanged, and that no new
  migration was generated.
- **L1** — round-trip every jsonb document type: deserialize a row persisted before the rename and assert
  no key was lost (a renamed property silently reads as `null`/default, so assert on values, not on
  successful deserialization).
- **L1/L2** — assert the HTTP routes and MCP tool names are byte-identical after the rename.
- **Build/test** — the rename compiles and the full suite passes.

## Acceptance Criteria

- No `memory*` table/column/index/PK/FK name is renamed, and the AGE graph name and labels are unchanged.
- The OpenAPI document diffs to zero changes.
- No JSON request/response field name is renamed.
- No jsonb key is renamed; a pre-rename row still deserializes with all values intact.
- No MCP tool name is renamed.
- The rename introduces no migration.

## Applies To

Goal 4 (the naming change is a vocabulary change, not a breaking one), LADR-05. `BR-45`.
