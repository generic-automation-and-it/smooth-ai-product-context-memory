# Architecture

## Persistence

The context-memory store is an **index over content**, not a content store. Document bodies live in
blob storage (see [ADR-0001](../hlds/adr-0001-blob-storage-backend-and-addressing.md)); PostgreSQL
holds metadata, relationships, and everything filtered on. The authoritative persistence model is
[ADR-0002](../hlds/adr-0002-persistence-layer-architecture.md).

### Three-level hierarchy

```
Initiative → MemoryGroup (tickets jsonb, repo columns) → Memory (logical) → MemoryVersion
                                                            ├── tags[] / facets[]  (unversioned)
                                                            └── blob address       (ADR-0001)
```

Seven entities (`Initiative`, `Label`, `MemoryGroup`, `GroupDescription`, `Memory`,
`MemoryVersion`, `MemoryLink`). Tags, facets, sources, repositories and tickets are deliberately
denormalised into columns / arrays / JSONB — they are not tables.

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

`repo` is not indexed yet — the retrieval query that would use it does not exist, so the index is
deferred (see ADR-0002).

`label.usage_count` is not a column — it is the derived `label_usage` view.

### Constraints

Hard: one current version per memory (partial unique index), version chain (unique `(memory_id,
version)`), logical identity (unique `uuid`), initiative always assigned (not-null FK). Soft (both
checked in the write path's read-before-write): subject uniqueness across a group, ticket
uniqueness across groups.
