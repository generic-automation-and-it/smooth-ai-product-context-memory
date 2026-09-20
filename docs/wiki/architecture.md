# Architecture

## Persistence

The context-memory store is an **index over content**, not a content store. Document bodies live in
blob storage (see [HLD 001](../hlds/001-context-memory-storage/)); PostgreSQL
holds metadata, relationships, and everything filtered on. Apache AGE is installed in the same
instance (`docker.io/apache/age:release_PG17_1.7.0` in both Aspire hosts). Relationships are graph
edges (`memory_graph`, vertex `Memory` identity-only, edge `:LINKS`) — see
[HLD 003](../hlds/003-graph-edges-on-age/).

### Provenance traversal

`POST /api/context/paths` answers *what chain of reasoning connects these two memories* — bounded
variable-depth paths, filterable by relation type and direction. The traversal and the endpoint
memories' descriptive fields come from **one** statement: `ag_catalog.cypher(...)` joined to `memory`
/ `memory_version` / `memory_group`, since SQL and Cypher share the session. Every traversal carries a
required depth bound (1–5); nothing in the storage layer stops an unbounded walk, so the application
does. Measured at depth 3 over 10,000 edges: 1.042 ms p95
([NFR-02](../hlds/003-graph-edges-on-age/nfrs/NFR-02-traversal-measurements.md)).

Whole-graph algorithms, centrality and recommendation are deliberately out of scope, as is mirroring
trackers, repositories or the agile hierarchy into the graph — those are trees with an authoritative
upstream, and a copy would need syncing. The authoritative persistence model is
[HLD 001](../hlds/001-context-memory-storage/). The write-path pipeline and the
agent-facing skill contract (the **sole authority** on the write path) are specified in
[HLD 002](../hlds/002-context-memory-write-pipeline/); the skill lives at
`.agents/skills/mimisbrunnr-context-memory/`.

### Three-level hierarchy

```
Initiative → MemoryGroup (tickets jsonb, repo columns) → Memory (logical) → MemoryVersion
                                                            ├── tags[] / facets[]  (unversioned)
                                                            └── blob address       (HLD 001)
```

Six entities (`Initiative`, `Label`, `MemoryGroup`, `GroupDescription`, `Memory`,
`MemoryVersion`). Relationships are AGE edges, not a seventh table. Tags, facets, sources,
repositories and tickets are deliberately denormalised into columns / arrays / JSONB — they are
not tables.

### Two stable/versioned pairs

`MemoryGroup → GroupDescription` and `Memory → MemoryVersion` are the same shape at different levels.
Both children are **append-only** — enforced by database triggers, never updated in place. A changed
claim on an unchanged subject is a version bump (a new `MemoryVersion` row with a higher version),
not an update.

### The subject/claim split

| Row | Field | Role | Lifecycle |
|---|---|---|---|
| `Memory` | `description` | **subject** — what deduplication matches on | stable |
| `MemoryVersion` | `statement` | **claim** — the fact asserted | volatile, versioned |
| `MemoryVersion` | `content_summary` | AI-generated TL;DR of the blob | versioned |

Tags and facets are classification, not claims, so they are **unversioned** and live on the logical
`Memory` row.

### Two time axes

| Axis | Field(s) | Meaning |
|---|---|---|
| Business time | `valid_from` / `valid_until` | when it was true in the world |
| System time | `created_on` | when we recorded it |

Neither derives from the other; removing `created_on` would make correcting a record
indistinguishable from the world changing.

### Search surface

Blob content is not indexable, so an AI-generated `content_summary` and keywords make memories
findable without their bodies being indexed. Indexes:

- **GIN** over `tags`, `facets`, `tickets` (containment)
- **GIST** over `tstzrange(valid_from, valid_until)`
- **full-text** over `name`/`description` and `statement`/`content_summary`
- **B-tree** over `kind`, `status`, `initiative_id`

`repo` is indexed — `IX_memory_group_repo` (B-tree, added 2026-09-10) — and the retrieval query filters
on it (see [HLD 001](../hlds/001-context-memory-storage/)).

`label.usage_count` is not a column — it is the derived `label_usage` view.

### Constraints

Hard: one current version per memory (partial unique index), version chain (unique `(memory_id,
version)`), logical identity (unique `uuid`), initiative always assigned (not-null FK). Soft (both to
be enforced by the write path's read-before-write once the skill exists — see
[HLD 001](../hlds/001-context-memory-storage/)): subject uniqueness across a group (exact-slug
duplicates are already rejected by the unique `(group_id, subject_slug)` backstop index), ticket
uniqueness across groups.
