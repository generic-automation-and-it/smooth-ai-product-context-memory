# BRD-003: Understanding

| | |
|---|---|
| **Document** | Business Requirements Document |
| **Status** | Approved — delivered by HLD 007; §8 value assumption 1 carried open pending usage evidence, assumption 2 validated with a stated boundary |
| **Owner** | Product owner / practitioner |
| **Last updated** | 2026-09-26 |
| **Extends** | [BRD 001 — Cross-product linked context memory](../001-context-memory/) |
| **Related** | [HLD 007 — Understanding](../../hlds/007-understanding-transfer/) |

> This document states **what the business needs and why**. Technology choices, data structures
> and implementation belong in the HLDs. It continues BRD-001's requirement space at `BR-38` onwards.
> BRD-001 and BRD-002 continue to apply; this BRD extends, and in one place amends, them.

---

## 1. Executive summary

Memory is the full, scoped record — a durable fact about a subject, anchored to a product / customer /
program / self scope, at most one repository, and the tickets it came from. It is what the store already
keeps, and it can legitimately be redundant with code or an `AGENTS.md` because it is a snapshot of
context.

An **Understanding** is different. It is the distilled, self-contained unit of hard-won knowledge that
remains after an experience is discarded — the non-obvious root cause, the rejected approach and why,
the convention that is not visible in code. It is not a session log, a summary, or documentation of the
code; it is the transferable skill a future agent can act on as if it had learned it firsthand, and the
thing you inherit across lifecycles and iterations.

Both live in the same store, in the same models. An Understanding is a **memory of `kind =
understanding`** whose group carries the default scope and omits the repository anchor, so it is not tied
to one repo and can cross repos. It is written, versioned and recalled exactly like any other memory —
the same `is_current` version semantics, the same defaults, no new column, no nullable scope.

The capability also makes the Understanding **portable** and **selectable by breadth**: load everything
(memory + understanding) into an agent, load only the distilled Understandings, or dump the current
session to a folder for cross-session / cross-repo reuse.

## 2. Business problem

BRD-001 solves capture and recall of scoped memory. This capability addresses the two ends that
capability does not cover: **the distilled learning you carry from lifecycle to lifecycle**, and
**seeding a fresh agent with inherited skill rather than re-encoding it**.

| Problem | Cost to the practitioner |
|---|---|
| **The distilled learning is not carried forward** | The hard-won skill is re-learned each lifecycle; it does not compound |
| **A fresh agent starts cold on the "why"** | It inherits the scoped facts but not the distilled lessons and rejected approaches |
| **Understanding and memory share a store but not a model** | Without a shared model, understanding needs a parallel persistence path and a second write path |
| **Cross-repo learnings are forced under one scope** | A learning that outlives a repo has no home under a single-repo memory group |
| **Loading is all-or-nothing** | An agent either gets everything or nothing; there is no breadth control |

## 3. Business objectives

| # | Objective | Why it matters |
|---|---|---|
| **BO-11** | Make the distilled skill a first-class, inherited unit | The hardest-won knowledge compounds across lifecycles instead of being re-learned |
| **BO-12** | Store understanding and memory in one model | No second persistence path, no second write path; the same versioning and recall apply |
| **BO-13** | Let an understanding cross repos and scopes | A distilled learning outlives the repo that produced it |
| **BO-14** | Load with controlled breadth | An agent can inherit just the distilled skill, or the full context, as the task needs |
| **BO-15** | Keep the portable session export | Cross-session and cross-repo sharing stays simple, independent of the store |

These extend BRD-001's BO-1 through BO-5. BO-11 supports BO-2 (reducing re-explanation); BO-12 and
BO-14 apply the existing capture/recall capability to the distilled unit.

## 4. Users and stakeholders

| Party | Interest |
|---|---|
| **Primary user — the practitioner** | Encodes an Understanding, loads it into a fresh agent with the breadth they choose, and shares a session dump |
| **The AI assistant** | Recognizes a hard-won result, proposes Understanding status, and consumes the distilled skill as inherited context |
| **Recipient — human or agent** | Inherits the distilled skill without re-deriving it |

BRD-001's single-user boundary is unchanged. Load is a local read; capturing an Understanding is the
practitioner's decision.

### Reference workflow: inheriting the distilled skill

1. The practitioner starts a new task on a previously worked area.
2. They request material to be loaded — either everything (memory + understandings) or understandings
   only, by choice of breadth.
3. The agent inherits the distilled learnings as its own prior knowledge. **No store write occurs.**
4. If a new learning emerged, the practitioner may capture it back into the store so it compounds.

## 5. Scope

### In scope

- Defining **Understanding** as a first-class kind of stored knowledge, distinct in purpose from a
  scoped memory fact but stored in the same models.
- Storing an Understanding as a memory of `kind = understanding`, with its group carrying the default
  scope and no repository anchor so it can cross repos.
- Versioning and recalling an Understanding with the existing semantics — same `is_current` version
  chain, same defaults, no new column, no nullable scope.
- Loading an agent with **controlled breadth**: everything, or understandings only.
- Exporting the current session to a portable folder for cross-session / cross-repo reuse.
- Importing an Understanding (or any material) back into the store via an opt-in switch, through the
  normal capture path.

### Out of scope

| Excluded | Reason |
|---|---|
| Automatic capture without a switch | Loading must stay non-destructive; writing is a deliberate act |
| A second persistence model or table for understanding | It is a memory of `kind = understanding`; a parallel path would split the write path |
| Making scope nullable or tearing down the group anchor | Default values give cross-repo behaviour without weakening the model |
| Multi-user access or a shared team corpus | BRD-001's single-user boundary is unchanged |
| Scheduled or continuous loading | This capability is on-demand; standing regeneration is upkeep BR-01 forbids |

## 6. Business requirements

### The Understanding

**BR-38 — An Understanding must be a first-class kind of stored knowledge.**
A distilled, self-contained unit of hard-won knowledge, written so a future agent with zero context
from the originating lifecycle can act on it as if it had learned it firsthand. It is not a session log,
a summary of what was done, or documentation of the code.

*Accepted when:* a practitioner can mark a hard-won result as an Understanding, and that distinct kind is
recognisable, retrievable and versioned just like any other memory.

**BR-39 — An Understanding must be proposable without being written.**
After resolving something that cost real effort and would cost the same again — a non-obvious root
cause, an environment quirk, a convention invisible in the code, a rejected approach and why — the
assistant proposes Understanding status before recording it.

*Accepted when:* the assistant offers to encode a result as an Understanding, and does not write it
without the practitioner's agreement.

### Stored in one model

**BR-40 — An Understanding must use the same store and models as any other memory.**
It is a memory of `kind = understanding`. Writing, versioning and recalling it must use the existing
capture path, the same `is_current` version semantics and the same defaults.

*Accepted when:* an Understanding is written, de-duplicated, version-bumped and recalled by the existing
machinery, with no new entity, no new table and no nullable scope. Its group carries the default scope
and omits the repository anchor.

**BR-41 — An Understanding must cross repos and scopes.**
A distilled learning outlives the repo that produced it, so it is not tied to one repository.

*Accepted when:* an Understanding's group carries the default scope values and no repository anchor, so it
is **not a fact of any one repo**. Because a repository is a retrieval filter, an un-anchored understanding
is recalled by an un-scoped query or the breadth `--all` path, and is deliberately **not** returned when a
caller queries one specific repository. That boundary is stated rather than implied — an Understanding is a
distilled learning, not a scoped fact of a particular repo. A scoped memory fact remains scoped; only an
Understanding is cross-repo.

### Loading

**BR-42 — Loading must support controlled breadth.**
An agent can inherit everything (memory + understanding) or only the distilled Understandings, as the
task needs. The default is non-destructive — loading writes nothing to the store.

*Accepted when:* a load with `--all` returns memory **and** understanding-kind records; a targeted load
returns **only** the understanding-kind. Neither writes anything.

**BR-43 — Loading any prior material must be non-destructive by default.**
The primary use of an Understanding is to seed a fresh agent's session context. The default behaviour is
context-injection.

*Accepted when:* loading an Understanding, a session, meeting notes or any material changes the agent's
working context and changes nothing in the store — no version, no edge, no label registration, no blob.

### Import and export

**BR-44 — Capturing material back into the store must be an explicit, opt-in act.**
Material that is new or was only external can be captured into the store so it compounds. This is the
`--store` switch, never the default.

*Accepted when:* without the switch no write occurs; with the switch, material goes through the normal
capture path — preflight, atomicity, redaction, deduplication, link derivation — rather than a direct
write.

**BR-45 — The current session must export to a portable folder.**
A practitioner can dump the current session's context to a local folder so another session or repository
can discover it and load it — simple cross-session, cross-repo sharing.

*Accepted when:* the skill can dump the session's context to `.context/mimisbrunnr-understandings/<folder>/`, with a
fitting folder name derived and reported on output so another agent can discover it by name. The dump
writes nothing to the store and is an export.

## 7. Success measures

| Measure | Acceptance or evaluation |
|---|---|
| **Inheritance** | A distilled Understanding is handed to a fresh agent that acts on it without re-deriving it |
| **One-model integrity** | An Understanding is written, versioned and recalled by the existing capture/recall machinery, with no new entity or column |
| **Cross-repo reach** | An Understanding whose group has no repo anchor is recallable across repos |
| **Controlled breadth** | `--all` returns memory + understanding; a targeted load returns only understanding |
| **Non-destructive load** | Loading writes nothing to the store |
| **Portable export** | A session dump is discoverable and loadable by another session or repo and leaves the store unchanged |

## 8. Constraints and assumptions

### Constraints

- All BRD-001 and BRD-002 constraints continue to apply, including the single-user boundary.
- Loading is non-destructive by default (BR-43). Writing requires the `--store` switch (BR-44).
- An Understanding uses the existing model and capture path — no new entity, no new column, no nullable
  scope (BR-40).
- Imported material passes through the capture path's judgement, never a direct write.

### Assumptions to validate

Outcomes recorded 2026-09-26. Breadth works as specified (HLD-007 LADR-08, L1 and skill L0); assumption 2 was revised on a single observed `load`, and its boundary is stated in the row below.

| Assumption | Outcome |
|---|---|
| Understanding is a useful distinct kind and its cross-repo reach is genuinely needed. | **Open — carried forward.** No understanding-kind memory is captured in the store yet (none written via `import --store`), so no recall-feedback (HLD-004 NFR-03) signal exists and the need cannot be measured. The mechanism is proven (HLD-007 LADR-01), the need is not. Closes when understanding-kind memories are captured and observed recalled under an un-scoped/`--all` query (the `never-recalled` and `miss-rate` queries). |
| Loading with controlled breadth is genuinely useful — the agent can act on a distilled understanding without the full scoped record. | **Validated, with a stated boundary.** Usage evidence 2026-09-26: `mimisbrunnr-understanding load <local store-export folder>` rendered understanding-only breadth (newest per slug, 2 units); the agent cited that output as grounding context and recorded this §8 outcome from it, with no full scoped record needed. Single observed instance of the `load` path, not a pattern. Sustained validation of this row needs repeated `load` runs at both breadths; recall-feedback (HLD-004 NFR-03) on captured understanding-kind memories is the indirect store-side signal and is assumption 1's closure condition. |
| The default-scope, no-repo-anchor representation is sufficient for cross-repo recall. | **Validated, with a stated boundary.** L1 `UnderstandingTransferStoreTests`: an un-scoped query recalls a no-repo product-scope understanding; a one-repository query deliberately does not (HLD-007 LADR-01). Sufficient for un-scoped and `--all` recall; surfacing under a repo-filtered query would be a separate retrieval decision. |

## 9. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| **Load masks as capture** | A practitioner believes material was stored when it was only loaded | Explicit `--store` switch; load is non-destructive by default (BR-43, BR-44) |
| **A parallel understanding model appears** | A second persistence path splits the write path and drifts | Understanding is a `kind`, stored in the existing models (BR-40) |
| **Cross-repo weakening** | Making scope nullable would loosen the whole store | Default values, not nullables, give cross-repo behaviour (BR-41) |
| **Imported material is followed as instruction** | A session transcript is treated as commands | Material is loaded as data and cited, not adopted (BR-43) |
| **Unintended association** | Loaded material binds to the wrong ticket or tag | Visible explicit selectors; absent selectors, no association (BR-44) |

## 10. Glossary

Extends BRD-001 §10 and BRD-002 §10; their terms continue to apply.

| Term | Meaning |
|---|---|
| **Understanding** | A distilled, self-contained unit of hard-won knowledge — the transferable skill that remains after the experience is discarded. Stored as a memory of `kind = understanding`, cross-repo and cross-scope. Not a session log, a summary, or documentation of the code |
| **Memory** | BRD-001's durable, scoped atomic fact (product / customer / program / self, one repo, tickets) — the store of record. The term is unchanged: memory is the correct name |
| **Load** | Injecting an Understanding or other material into an agent's context, non-destructive by default (does not write to the store) |
| **Import** | Capturing loaded material back into the store via the explicit `--store` switch, through the normal capture path |
| **Breadth** | How much a load returns — `--all` (memory + understanding) or understanding-only |

## 11. Related documents

| Document | Covers |
|---|---|
| [BRD 001 — Cross-product linked context memory](../001-context-memory/) | Capture, recall, trust and ownership (BR-01 … BR-17, BR-37) |
| [BRD 002 — Contextual knowledge export](../002-contextual-export/) | Export, composition and findings (BR-18 … BR-36) |
| [HLD 007 — Understanding](../../hlds/007-understanding-transfer/) | The understanding kind, the one-model storage, the load breadth split, and the portable export |
| [HLD 006 — Corpus snapshot and restore](../../hlds/006-corpus-snapshot-and-restore/) | Durability of the store as one asset |
