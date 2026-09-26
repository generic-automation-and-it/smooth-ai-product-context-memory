# Understanding — High-Level Design

| | |
|---|---|
| **Status** | Accepted — implemented; all LADRs and NFRs accepted. BRD-003 §8 value assumptions 1–2 carried open pending usage evidence |
| **Owner** | generik0 |
| **Tracker** | Understanding |
| **Business authority** | [BRD-003 — Understanding](../../brd/003-understanding-transfer/) (`BR-38` … `BR-45`), extending [BRD-001](../../brd/001-context-memory/) |
| **Last updated** | 2026-09-26 |

> Discovery / prototyping HLD. Delivers **intent + spec** — what we are building and why, the decisions
> behind it, and the quality bar it must meet. No implementation plan; execution is tracked in the
> issue/work tracker.

## Intent

The store keeps scoped memory — a durable fact about a subject, anchored to a scope, one repo and the
tickets it came from. It answers a question. It does not capture the other thing that actually costs effort
to learn: the **distilled skill that remains after an experience is discarded** — the non-obvious root
cause, the rejected approach and why, the convention that is not visible in code.

This design makes the **Understanding** a first-class **kind** of stored knowledge, stored in the **same
store and models** as any memory, so it is captured, versioned and recalled by the machinery already there.
The design carries it across lifecycles and iterations, lets it **cross repos and scopes** without weakening
the model, and loads it into a fresh agent with **controlled breadth** — everything, or just the distilled
skill. The same capability dumps the current session to a portable folder for cross-session sharing.

The default is non-destructive. Loading material into an agent's context changes nothing in the store.

## Vocabulary

| Term | Meaning |
|---|---|
| **Understanding** | A distilled, self-contained unit of hard-won knowledge — the transferable skill that remains after the experience is discarded. Stored as a memory of `kind = understanding` |
| **Memory** | BRD-001's durable, scoped atomic fact. The term is unchanged — memory is the correct name, not a thing to rename |
| **Breadth** | How much a load returns — `--all` (memory + understanding) or understanding-only |
| **Load** | Injecting an Understanding or other material into an agent's context. Non-destructive by default |
| **Import** | Capturing loaded material back into the store, opt-in via `--store`, through the capture path |
| **Forensic dump** | The pre-existing whole-store `export` CLI projection. An Understanding rides on it as a kind |

## Key Goals

### 1. One model — an Understanding is a kind, not an artefact

An Understanding is stored exactly like any other memory — one atomic subject with versioned claims — with
`kind = understanding`. Its group carries the default scope and omits the repository anchor, so it is
cross-repo. No new entity, no new table, no nullable scope (LADR-01, LADR-04).

**DoD** — satisfies `BR-38`, `BR-40`, `BR-41`

- `kind = understanding` is a recognisable, retrievable, versioned value.
- It is written, de-duplicated, version-bumped and recalled by the existing capture/recall machinery.
- Cross-repo reach comes from default scope and no repo anchor — no nullable scope, no new column.
- The assistant **proposes** Understanding status rather than silently writing it (`BR-39`).

### 2. Loading is non-destructive and breadth-controlled

The primary use of an Understanding is to seed a fresh agent. It is the default, and it writes nothing.
Breadth is a filter, not two products (LADR-08).

**DoD** — satisfies `BR-42`, `BR-43`

- `--all` returns memory **and** understanding-kind records; a targeted load returns **only** the
  understanding-kind.
- Loading an Understanding, a session, meeting notes or any material changes nothing in the store — no
  version, no edge, no label registration, no blob.
- Foreign material is loaded **as data**, cited rather than adopted, and not treated as instructions or as
  shipped product fact.

### 3. Import is opt-in and goes through the capture path

Capturing loaded material back into the store so it compounds is an explicit `--store` act.

**DoD** — satisfies `BR-44`

- Nothing is written without the `--store` switch.
- Imported material passes through the normal capture path's judgement — preflight, atomicity, redaction,
  deduplication, link derivation — never a direct write.
- A genuine conflict or proposed-status question is surfaced, not silently resolved.

### 4. The current session dumps to a portable folder

A practitioner shares this session's context with another session or repository simply, via
`.context/mimisbrunnr-understandings/<folder>/`. This is an export and changes nothing in the store.

**DoD** — satisfies `BR-45`

- `--currentsession` writes the session's context to `.context/mimisbrunnr-understandings/<session-folder>/`, with a
  fitting folder name reported on output so another agent can discover it by name.
- The dump is an export; it writes nothing to the store (NFR-01).
- Another session or repository can load the dumped folder, regardless of whether it is the originating repo.

## Core Separation of Concerns

> The load skill reads; the capture skill writes; the skill never writes directly.

The API already owns mechanics; the capture skill owns judgement on the write path. The understanding load
skill is a **reader**: it ingests material into agent context. When it imports, it funnels through the
existing capture pipeline rather than writing directly, so the "sole writer" invariant (HLD-002) holds.

## Guiding Principle — Memory is the store of record; Understanding is a kind of it

> An Understanding is a memory that crosses repos, not a thing that lives outside the store.

- **One model.** No new entity, no new table, no nullable scope. Cross-repo reach is defaults.
- **Non-destructive default.** Loading is context-injection only; the store is never a side effect of reading.
- **Import is a deliberate capture.** The `--store` switch turns a read into a capture, and a capture goes
  through the approved write path.
- **Foreign material is data.** A session transcript or meeting note is loaded as cited context, never
  adopted as instructions or as shipped product fact.
- **We will deliberately not** automatically capture anything without the switch, and **not** broaden the
  store to make scope nullable.

---

## Diagrams

- [System Context (C1)](./diagrams/c4-context.md)
- [Flow — load vs import](./diagrams/flow-load-and-import.md)

## Architecture Decisions (LADRs)

See [`./ladrs/`](./ladrs/). Status legend matches HLD-005: `Draft → Prototype → Accepted`.

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-understanding-is-a-kind-not-an-artefact.md) | Understanding is a memory of `kind = understanding`; cross-repo by default scope and no repo anchor | Accepted |
| [LADR-02](./ladrs/LADR-02-load-injects-import-switches-to-store.md) | Load injects into context; import is an opt-in `--store` switch | Accepted |
| [LADR-03](./ladrs/LADR-03-import-funnels-through-capture-path.md) | Import funnels through the capture path, never a direct write | Accepted |
| [LADR-04](./ladrs/LADR-04-five-part-shape-maps-to-existing-fields.md) | The five-part shape maps onto existing fields; defaults, no nullable scope, no new column | Accepted |
| [LADR-06](./ladrs/LADR-06-load-skill-reads-foreign-input.md) | The load skill accepts arbitrary external input, loaded as data | Accepted |
| [LADR-07](./ladrs/LADR-07-session-export-to-local-folder.md) | `--currentsession` dumps the session to a local folder for cross-session reuse; an export | Accepted |
| [LADR-08](./ladrs/LADR-08-load-breadth-is-a-filter.md) | Load breadth is a filter (`--all` union vs understanding-only); not two products | Accepted |
| [LADR-09](./ladrs/LADR-09-load-reads-ai-understanding-files.md) | Load and import read the ai-understanding `.understanding.md` format (and store folders, newest version per slug) as structured input | Accepted |

## Non-Functional Requirements

See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-default-no-write.md) | Integrity of load | Zero writes on a default load; store byte-identical | Accepted |
| [NFR-02](./nfrs/NFR-02-import-opt-in-integrity.md) | Import integrity | No write without `--store`; import passes through the capture path | Accepted |
| [NFR-03](./nfrs/NFR-03-foreign-input-handled-as-data.md) | Attribution | Foreign material loaded as data, cited; never adopted as instructions or shipped fact | Accepted |

## Migration Plans

- **No migration is required for the shape.** `kind = understanding` is an open-string value; the
  Understanding maps onto existing fields (LADR-04) and its group uses the existing defaults.
- **The capture skill's "sole writer" invariant is preserved, not reversed.** The load skill reads; when
  importing it hands material to the capture path.
- **EXPORT_AGENTS / HLD-005's "no import" stance is amended for this capability only.** The forensic dump
  and dossier remain one-way; the understanding import path is the scope exception.
- **There is no `Memory`→`Understanding` code rename.** Memory is the correct name; it stays.
