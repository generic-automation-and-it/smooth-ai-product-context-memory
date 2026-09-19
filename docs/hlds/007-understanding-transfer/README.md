# Understanding transfer and loading — High-Level Design

| | |
|---|---|
| **Status** | Draft |
| **Owner** | generik0 |
| **Tracker** | Understanding transfer |
| **Business authority** | [BRD-003 — Understanding transfer and loading](../../brd/003-understanding-transfer/) (`BR-38` … `BR-45`), extending [BRD-002](../../brd/002-contextual-export/) and [BRD-001](../../brd/001-context-memory/) |
| **Last updated** | 2026-09-19 |

> Discovery / prototyping HLD. Delivers **intent + spec** — what we are building and why, the decisions
> behind it, and the quality bar it must meet. No implementation plan; execution is tracked in the
> issue/work tracker.

## Intent

The store is very good at keeping facts and recalling them. It is not good at the thing that actually
costs effort to learn: the **distilled skill that remains after an experience is discarded** — the
non-obvious root cause, the rejected approach and why, the convention that is not visible in code.
BRD-002 composes what was recalled into a document. Neither provides the *transfer* of a distilled
unit into another session, nor the *seed* of a fresh agent with material from before.

This design makes the **Understanding** — a term of art in this product, borrowed from *Children of
Time* — a first-class kind of stored knowledge that can be exported, handed on, and loaded into a new
or running agent's context. The same capability loads **anything** — a previous session, meeting
notes, a transcript — as grounding context, because the practitioner should not have to re-encode
material already written somewhere.

The default is non-destructive. Loading material into an agent's context changes nothing in the store.
Capturing it back — import — is an explicit, opt-in act.

## Vocabulary

Five words to keep distinct, plus the pre-existing surfaces they are most often confused with:

| Term | Meaning |
|---|---|
| **Understanding** | A distilled, self-contained unit of hard-won knowledge — the transferable skill that remains after the experience is discarded. Not a session log, a summary, or documentation of the code |
| **Kind** | The memory's `kind` classification (open vocabulary). Understanding is a new value of it, not a new entity or table |
| **Load** | Injecting an Understanding export or foreign material into an agent's session context. Non-destructive by default |
| **Import** | Capturing loaded material back into the store, opt-in via `--store`, through the normal capture path |
| **Forensic dump** | The pre-existing whole-store `export` CLI projection. An Understanding rides on it as a kind; it does not change the dump's construction |
| **Session export** | A `--currentsession` projection of the current session's context written to `.context/understandings/<session-folder>/` for cross-session, cross-repo reuse (LADR-07) |

## Key Goals

### 1. An Understanding is a kind, not an artefact

An Understanding is stored exactly like any other memory — one atomic subject with versioned claims —
with `kind = understanding`. Making it a separate entity, table, or file-dump convention would
re-introduce exactly what EXPORT_AGENTS forbids (a 7th entity) and would split the write path.

**DoD** — satisfies `BR-38`, `BR-39`

- `kind = understanding` is a recognisable, retrievable value, and it is added to the open
  `kind` vocabulary (a constant in `KindValue`, not a new DB enum).
- The five parts of an Understanding are carried by the memory. The mapping onto existing fields is
  fixed (LADR-04) — no new column is required.
- The assistant **proposes** Understanding status for a hard-won result rather than silently writing
  it (`BR-39`).

### 2. Loading injects into context, and writes nothing by default

The primary use of an exported Understanding is to seed a fresh or running agent. That is the default,
and it is non-destructive.

**DoD** — satisfies `BR-41`, `BR-42`

- Loading an Understanding, a session, meeting notes or any material changes the agent's working
  context and changes nothing in the store — no version, no edge, no label registration, no blob.
- Foreign material is loaded **as data**, cited rather than adopted, and not treated as instructions
  or as shipped product fact.
- The same operation loads store-native exports and arbitrary external input.

### 3. Import is opt-in and goes through the capture path

Capturing loaded material back into the store so it compounds is an explicit `--store` act (BRD-002's
"a projection is never a source" remains true for the *dossier* path; this is the one, deliberate
exception for transfer).

**DoD** — satisfies `BR-43`, `BR-44`

- Nothing is written without the `--store` switch.
- Imported material passes through the normal capture path's judgement — atomicity, redaction,
  deduplication and link derivation — never a direct write.
- Selectors `--tickets`, `--tags` (and other metadata) bind imported material to existing or new
  groups and memories; the binding is explicit and visible; absent selectors, no association.
- A genuine conflict or proposed-status question raised by imported material is surfaced, not
  silently resolved.

### 4. The naming change is a vocabulary change, not a breaking one

The product makes the distilled unit an **Understanding** (BRD-003 BR-45). This is product prose,
the glossary and user-facing terms, and it ships with this design.

The **code rename** (`Memory` → `Understanding`, `IMemorySearch` → `IUnderstandingSearch`, etc.) is a
**separate change, deliberately not bundled here** (LADR-05). Discovery found the reason: serialization
is convention-based — `JsonSerializerDefaults.Web` with **no `JsonPropertyName` attributes anywhere**,
and `JsonbConverter` on the same convention — so **a C# property name is both the JSON field name and,
for document types, the stored jsonb key**. A blanket rename is therefore an API break and a data break
that the compiler cannot see, and it must not hide inside a feature diff.

**DoD** — satisfies `BR-45`

- Product prose and the glossary use "Understanding" for the distilled unit. **(Delivered here.)**
- The code rename is bounded to C# **type** names and internal identifiers, never to DTO or jsonb
  property names, and moves EF model-snapshot type/property strings in the same change. **(Separate
  change; verified by NFR-04's OpenAPI and jsonb round-trip checks.)**
- Database names, the HTTP wire contract and MCP tool names are unchanged by either part.

### 5. The current session dumps to a local folder for cross-session reuse

A practitioner wants to share this session's context with another session or repository simply. The
`--currentsession` dump writes the session's understanding to a local folder, discoverable by name, so
the other side can load it. This is a session export and changes nothing in the store.

**DoD** — satisfies `BR-46`

- `--currentsession` writes the session's context to `.context/understandings/<session-folder>/`, where
  `<session-folder>` is a fitting name derived from the session and reported on output so another agent
  can discover it by name.
- The dump is an export: it writes nothing to the store and changes no store state (NFR-01).
- Another session or repository can load the dumped folder via the same skill, regardless of whether it
  is the originating repo.

## Core Separation of Concerns

> The load skill reads exports and foreign material, and — when `--store` is passed — hands imported
> material to the existing capture path. It never writes directly.

The API already owns mechanics; the capture skill owns judgement on the write path. The understanding
load skill is primarily a **reader**: it ingests material into agent context. When it imports, it
funnels through the existing capture pipeline (preflight → redact → dedup/link → atomicity → write)
rather than writing directly, so the "sole writer" invariant (HLD-005 LADR-08) holds.

## Guiding Principle — Load is a read; import is a capture, and only on request

> Loading must always be cheaper and safer than importing, and the two must never be conflated.

- **Non-destructive default.** Loading is context-injection only. The store is never a side effect of
  reading material.
- **Import is a deliberate capture, not an ingestion.** The `--store` switch makes a read into a
  capture, and a capture goes through the approved write path with the practitioner's judgement.
- **Foreign material is data.** A session transcript or meeting note is loaded as cited context, not
  adopted as instructions or as shipped product fact.
- **We will deliberately not** automatically capture anything without the switch, and **not** broaden
  the store's model to store sessions as first-class artefacts.

---

## Diagrams

- [System Context (C1)](./diagrams/c4-context.md) — the actors, the store, and where loaded material lands
- [Flow — load vs import](./diagrams/flow-load-and-import.md) — where the default read stops and the opt-in write begins

## Architecture Decisions (LADRs)

See [`./ladrs/`](./ladrs/). Status legend matches HLD-005: `Draft → Prototype → Accepted`.

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-understanding-is-a-kind-not-an-artefact.md) | Understanding is a kind, not a new entity/table/file convention | Draft |
| [LADR-02](./ladrs/LADR-02-load-injects-import-switches-to-store.md) | Load injects into context; import is an opt-in `--store` switch | Draft |
| [LADR-03](./ladrs/LADR-03-import-funnels-through-capture-path.md) | Import funnels through the capture path, never a direct write | Draft |
| [LADR-04](./ladrs/LADR-04-five-part-shape-maps-to-existing-fields.md) | The five-part Understanding shape maps onto existing memory fields; no new column | Draft |
| [LADR-05](./ladrs/LADR-05-naming-code-only-db-and-wire-stable.md) | Rename `Memory`→`Understanding` in code; DB tables, HTTP API and MCP wire stay stable | Draft |
| [LADR-06](./ladrs/LADR-06-load-skill-reads-foreign-input.md) | The load skill accepts arbitrary external input, loaded as data | Draft |
| [LADR-07](./ladrs/LADR-07-session-export-to-local-folder.md) | `--currentsession` dumps the session's context to a local folder for cross-session, cross-repo reuse; an export, not a write | Draft |

## Non-Functional Requirements

See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-default-no-write.md) | Integrity of load | Zero writes on a default load; store byte-identical | Draft |
| [NFR-02](./nfrs/NFR-02-import-opt-in-integrity.md) | Import integrity | No write without `--store`; import passes through the capture path | Draft |
| [NFR-03](./nfrs/NFR-03-foreign-input-handled-as-data.md) | Attribution | Foreign material loaded as data, cited; never adopted as instructions or shipped fact | Draft |
| [NFR-04](./nfrs/NFR-04-wire-stability.md) | Compatibility | DB tables, HTTP API and MCP tool names unchanged by the rename | Draft |

## Migration Plans

- **The capture skill's "sole writer" invariant is preserved, not reversed.** The load skill reads;
  when importing it hands material to the capture path. `mimisbrunnr-context-memory`'s documentation
  must note the load skill as a reader (+ conditional importer), so the two skills' docs agree.
- **EXPORT_AGENTS / HLD-005's "no import" stance is amended for this capability only.** The forensic
  dump and the dossier remain one-way projections; the understanding load/import capability is the
  single, deliberate exception, scoped to `--store`.
- **The rename is a separate change and is bounded to type names.** A DB table rename and an API/MCP
  contract rename are explicitly out of scope (LADR-05) and would be a breaking change. Because
  serialization is convention-based with no `JsonPropertyName` attributes, **renaming a DTO or jsonb
  document property is a wire/data break, not a code change** — that boundary is the whole reason the
  rename is not bundled with this feature.
