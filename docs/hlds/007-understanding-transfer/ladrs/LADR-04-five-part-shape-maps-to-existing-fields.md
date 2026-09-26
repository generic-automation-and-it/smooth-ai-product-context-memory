# LADR-04: The Understanding maps onto existing memory fields; defaults, no new column

**Status:** Accepted

## Context

An Understanding has five parts: **trigger** (the situation in which it applies), **the knowledge** (direct
operational guidance), **why** (the reasoning or failure that produced it, enough to judge edge cases),
**boundaries** (where it stops applying) and **provenance** (date and what it was learned from).

The question is whether these need new stored fields. The whole point of LADR-01 is that an Understanding
is a memory, so the re-use instinct is right. The one part that does not cleanly map is **trigger** — a
recognition condition that is more operational than a subject description.

The user's requirement is explicit: reuse existing fields, and where the model does not already carry a
value, fall back to **defaults** rather than nullable columns or new ones. The local store may be discarded,
so no residual data constrains the shape.

## Decision

Map the five parts onto existing fields, and add **no new column** and **no nullable scope**:

| Understanding part | Stored field |
|---|---|
| **Answer** (formerly *the knowledge*) | `Statement` |
| **Why** | `ContentSummary` (AI TL;DR of the reasoning/failure) |
| **Question** (formerly *trigger*) | `Description` — the question a reader has when the Understanding applies |
| **Boundaries** | `ValidUntil` + the group's scope |
| **Provenance** | `Sources` + `ValidFrom` + `CreatedOn` |

**Trigger maps to `Description`** — the one judgement call, chosen to avoid a schema change. If use proves a
dedicated field is needed, that is a follow-up validated by use, not a decision made ahead of evidence.

**Defaults, not nullables.** The group carrying the Understanding takes its default `ScopeDimension`
(`product` is the entity default) and omits the repository anchor. Versioning uses the existing `is_current`
swap — no new delta column; the version chain *is* the delta, computed from the previous version. No column
is made nullable, and no column is added.

## Alternatives Considered

- **Add a dedicated nullable `Trigger` column** — rejected for now: it is a schema change and `Description`
  already carries the situation. Flagged as the follow-up if use proves it insufficient.
- **Make `ScopeDimension` nullable** — rejected: default values give cross-repo reach without weakening the
  model (see LADR-01).
- **A new persisted delta column** — rejected for now: the `is_current` version chain already records change
  over time; an explicit delta would be redundant with it.
- **Flatten trigger into `Statement`** — rejected: the knowledge must stay clean operational guidance.
- **Store the five parts as a JSON document** — rejected: a JSON blob is not queryable by label or similarity
  and breaks the atomic-fact model.

## Consequences

- **2026-09-24 amendment — vocabulary.** *Trigger* and *the knowledge* are renamed **question** and
  **answer**, matching the `ai-understanding` file format, which made the unit an explicit
  question/answer pair so a trigger cannot be broader than its body. Stored fields are unchanged; the
  load renderer prints `Question:` / `Answer:` and still reads a legacy `trigger` key. See LADR-09 for
  the file-format mapping.

- No migration is required for the Understanding shape (the `kind` constant is a value, not a schema change).
- An Understanding is rendered from the same fields as any memory, so the export path needs no new shape
  handling beyond recognising the kind.
- Version semantics are unchanged; the current-version chain already gives the delta.
- The `trigger`→`Description` mapping is the single judgement call; it is recorded here so it can be
  revisited if use demands a dedicated field.

## Evidence (2026-09-26)

L0 `ExportRendererTests.Understanding_kind_renders_with_all_five_parts`; L1 `tests/SmoothAiProductContextMemory.Application.ComponentTest/Features/UnderstandingTransferStoreTests.cs`: a restatement carrying the uuid is a version bump with exactly one current version (the existing `is_current` swap), no new column, and `kind` stays open vocabulary (an unlisted kind validates).
