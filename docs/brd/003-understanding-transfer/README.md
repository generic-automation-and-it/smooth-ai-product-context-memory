# BRD-003: Understanding Transfer and Loading

| | |
|---|---|
| **Document** | Business Requirements Document |
| **Status** | Draft |
| **Owner** | Product owner / practitioner |
| **Last updated** | 2026-09-19 |
| **Extends** | [BRD 002 — Contextual knowledge export](../002-contextual-export/), and through it [BRD 001 — Cross-product linked context memory](../001-context-memory/) |
| **Related** | [HLD 007 — Understanding transfer](../../hlds/007-understanding-transfer/) |

> This document states **what the business needs and why**. Technology choices, data structures
> and implementation belong in the HLDs. It continues BRD-002's requirement space at `BR-38` onwards.
> BRD-001 and BRD-002 continue to apply; this BRD extends and, in two places, amends them.
---

## 1. Executive summary

A practitioner accumulates hard-won knowledge — how a thing got to be the way it is, why a rejected
approach was rejected, the non-obvious root cause, the convention that is not visible in code. That
knowledge is the most valuable thing the store holds, and it is also the most fragile: it survives
only inside the practitioner's head or an agent's ephemeral working context.

This capability makes that knowledge **portable and reusable**. It promotes the distilled, reusable
unit — an **Understanding** — to a first-class thing the store can hold, export and hand to another
agent or person. The same capability lets a practitioner load **any** captured or foreign material —
a previous session, meeting notes, a transcript — into a fresh agent's working context so the agent
starts already knowing what took real effort to learn. Optionally, and only by explicit choice, that
material can be captured back into the store so it compounds.

The default is deliberately non-destructive. Loading material into an agent's context changes nothing
in the store; capturing it back is a separate, explicit act.

## 2. Business problem

BRD-001 solves capture and recall. BRD-002 solves composing what was recalled into one document. This
capability addresses the two ends neither covers: **carrying the distilled skill out of one session
and into another**, and **seeding a fresh session with prior material without first re-encoding it**.

| Problem | Cost to the practitioner |
|---|---|
| **Hard-won knowledge is not reusable** | A non-obvious root cause or a rejected approach is re-learned, or lost when the session that held it ends |
| **A fresh agent starts cold** | Re-entering a repository means re-supplying context that was already distilled once |
| **Session traces are not loadable** | Meeting notes, transcripts and previous sessions are readable as files but not usable as starting context |
| **Knowledge does not travel between people** | The distilled understanding stays in the head of the practitioner who earned it |
| **Import and export are asymmetric** | The store reliably emits projections but has no way to ingest existing documentation or session material without re-typing it |

## 3. Business objectives

| # | Objective | Why it matters |
|---|---|---|
| **BO-11** | Make the distilled skill a first-class, portable unit | The hardest-won knowledge can be exported and handed on, not merely summarized |
| **BO-12** | Load existing material into a fresh agent's context by default | Re-entry and handover start from prior material without first re-encoding it |
| **BO-13** | Optionally capture loaded material back into the store | Material that compounds this session becomes compoundable for the next |
| **BO-14** | Keep the load-to-context path non-destructive by default | Reading and using material must never silently alter the store |
| **BO-15** | Accept arbitrary external input | Sessions, meeting notes and transcripts are usable, not just store-native exports |

These extend BRD-001's BO-1 through BO-5 and BRD-002's BO-6 through BO-10. BO-11 supports BO-2
(reducing re-explanation); BO-12 and BO-13 apply BO-2 and BO-3 to context seeding; BO-14 preserves
BO-5 (trust and ownership) and BRD-002's integrity stance.

## 4. Users and stakeholders

| Party | Interest |
|---|---|
| **Primary user — the practitioner** | Requests an understanding export, loads it into a fresh or running agent, or captures material into the store |
| **The AI assistant** | Consumes loaded material as contextual grounding; recognizes when a hard-won result deserves Understanding status |
| **Recipient — human or agent** | Receives a portable Understanding and acts on it without re-deriving it |

BRD-001's single-user boundary is unchanged. Load is a local operation; capture into the store
remains the practitioner's decision under a switch.

### Reference workflow: loading context into a fresh agent

1. The practitioner starts a new task on a previously worked area.
2. They request that prior material be loaded — a store-native understanding export, or a foreign
   document such as a previous session, meeting notes or transcript.
3. The agent ingests the material as grounding context. **No store write occurs.**
4. If the material is new, the practitioner may choose to capture it back into the store so it
   accumulates. This is the `--store` switch, an explicit act.

## 5. Scope

### In scope

- Defining **Understanding** as a first-class kind of stored knowledge, distinct from a session log
  or documentation of code — the transferable skill that remains after the experience is discarded.
- Exporting Understandings as part of the store's export surface (the whole-store projection **and**
  the contextual document), so a distilled unit can be handed on.
- Loading an Understanding export, or any foreign material, into a new or running agent's session
  context by default.
- Optionally capturing that loaded material back into the store via an explicit switch.
- Selecting, on import, how loaded material links to existing work — by ticket, by tag, or by other
  metadata.
- Naming the product vocabulary around **Understanding** so the concept reads as a unit of knowledge
  rather than a mechanical storage record.

### Out of scope

| Excluded | Reason |
|---|---|
| Automatic capture into the store without a switch | Load must stay non-destructive by default; writing is a deliberate act (BO-14) |
| Rewriting the store's memory model to store sessions as first-class artefacts | Load breadth is about *input*, not the store's internal model |
| Automatic decomposition of arbitrary documents without the practitioner's judgement | Atomicity and redaction on imported material need the capture path's judgement |
| Multi-user access or a shared team corpus | BRD-001's single-user boundary is unchanged |
| Scheduled or continuous loading | This capability is on-demand; standing regeneration is upkeep BR-01 forbids |

## 6. Business requirements

### The Understanding

**BR-38 — An Understanding must be a first-class kind of stored knowledge.**
A distilled, self-contained unit of hard-won knowledge, written so a future agent with zero context
from the originating session can act on it as if it had learned it firsthand. It is not a session
log, a summary of what was done, or documentation of the code — it is the transferable skill that
remains after the experience is discarded.

*Accepted when:* a practitioner can mark a hard-won result as an Understanding, and that distinct
kind is recognisable and retrievable. An Understanding carries the five parts — **trigger** (the
situation in which it applies), **the knowledge** (direct operational guidance), **why** (the
reasoning or failure that produced it, enough to judge edge cases), **boundaries** (where it stops
applying) and **provenance** (date and what it was learned from).

**BR-39 — An Understanding must be proposable without being written.**
After resolving something that cost real effort and would cost the same again — a non-obvious root
cause, an environment quirk, a convention invisible in the code, a rejected approach and why — the
assistant should propose Understanding status before persisting it.

*Accepted when:* the assistant offers to encode a result as an Understanding, and does not write it
without the practitioner's agreement.

### Export

**BR-40 — Understanding exports must be part of the export surface.**
A distilled Understanding is portable and handable-on. It appears in the whole-store projection and
is selectable in the contextual document.

*Accepted when:* an exported Understanding is readable without the application and preserves the
five parts with their provenance. The Understanding is identifiable as a dated snapshot, not a
guarantee of current truth when read later (inherits BR-21).

### Loading

**BR-41 — Loading must inject material into an agent's context without writing to the store by default.**
The primary use of an exported Understanding is to seed a new or running agent's session so the agent
can act on prior knowledge. The default behaviour is context-injection.

*Accepted when:* loading an Understanding, a session, meeting notes or any material changes the
agent's working context and changes nothing in the store. No version, no edge, no label registration,
no blob is written.

**BR-42 — Loading must accept arbitrary external input, not only store-native exports.**
The same capability loads a previous session, meeting notes or any transcript, so the practitioner
does not have to re-encode material already written somewhere.

*Accepted when:* a practitioner can load a foreign document and have it available as grounding
context, with its provenance and (where it is a store export) its attribution preserved. Foreign
material is loaded as data, not as instructions the agent must obey.

### Import

**BR-43 — Capturing loaded material back into the store must be an explicit, opt-in act.**
Material that is new, or that was previously only external, can be captured into the store so it
compounds. This is the `--store` switch and is never the default.

*Accepted when:* without the switch, no write occurs. With the switch, loaded material goes through
the normal capture path — its atomicity, redaction, deduplication and link derivation applied — rather
than being written directly. A genuine conflict or proposed-status question raised by imported
material is surfaced, not silently resolved.

**BR-44 — Import must support linking loaded material to the work it belongs to.**
Selectors such as `--tickets` and `--tags` (and other metadata) associate loaded material with
existing or new groups and memories.

*Accepted when:* a practitioner can bind loaded material to the relevant tickets and tags; the
binding is explicit and visible; and absent selectors, the material is captured without unintended
association.

### Session export

**BR-46 — The understanding capability must export the current session's context as a portable folder.**
A practitioner can dump the current session's understanding to a local folder so another session or
repository can discover it and load it — simple cross-session, cross-repo context sharing. This is a
session export: a projection of what this session holds, written for reuse, not the store.

*Accepted when:* the skill can dump the current session's context to `.context/understandings/<folder>/`,
where the folder name is derived from the session so another agent can discover it by name. The dump
changes nothing in the store (it is an export, not a write). Another session or repository can then
load the dumped folder.

### Naming

**BR-45 — The product vocabulary must name the distilled unit "Understanding", not "memory".**
The concept reads as a unit of knowledge and skill, not a mechanical storage record. This naming
applies to product prose, the glossary and user-facing terms.

*Accepted when:* the README and product prose describe the distilled unit as an Understanding.
Code identifiers, database table names and the public API/MCP wire contract retain their existing
names so the naming change does not become a breaking change.

## 7. Success measures

| Measure | Acceptance or evaluation |
|---|---|
| **Reusability** | A distilled Understanding is handed to a fresh agent that then acts on it without re-deriving the knowledge |
| **Non-destructive load** | Loading material into an agent's context leaves the store byte-identical |
| **Foreign-input usability** | A session, meeting note or transcript is loadable and usable as grounding context |
| **Opt-in integrity** | Nothing is written to the store without the `--store` switch |
| **Attribution** | Imported material preserves its provenance and is not adopted as agent instructions or as shipped product fact |
| **Naming consistency** | Product prose uses "Understanding" for the distilled unit; code/DB/wire keep stable names |
| **Cross-session sharing** | A session dump written to the local folder is discoverable and loadable by another session or repository, and leaves the store unchanged (BR-46) |

## 8. Constraints and assumptions

### Constraints

- All BRD-001 and BRD-002 constraints continue to apply, including the single-user boundary and
  database-as-truth.
- Loading is non-destructive by default (BR-41). Writing requires the `--store` switch (BR-43).
- Imported material always passes through the normal capture path's judgement (atomicity, redaction,
  dedup, link derivation), never a direct write.
- Understanding exports are projections like any other export; a stale or edited copy is not
  authoritative.

### Assumptions to validate

- Understanding is a useful distinct kind and its five-part shape is the right shape. Whether each
  part maps cleanly onto existing stored fields is answered by use and the HLD.
- Loading a representative session, meeting note or transcript is genuinely useful — i.e. the
  material is not so voluminous that it overwhelms the agent's context.
- The selectors `--tickets`/`--tags` are sufficient to bind imported material usefully.

## 9. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| **Load masks as capture** | A practitioner believes material is stored when it was only loaded | Explicit `--store` switch; load is non-destructive by default (BR-41, BR-43) |
| **Imported material is followed as instruction** | A session transcript or note is treated as commands | Loaded material is loaded as data and cited, not adopted (BR-42) |
| **Foreign material breaks atomicity or leaks a secret** | A bundled or sensitive capture pollutes the store | Imported material passes through the capture path's atomicity and redaction (BR-43) |
| **Unintended association** | Loaded material is bound to the wrong ticket or tag | Visible explicit selectors; absent selectors, no association (BR-44) |
| **Naming change breaks the wire** | Renaming the unit across the API and DB breaks consumers | Code identifiers, DB tables and the API/MCP wire keep stable names (BR-45) |

## 10. Glossary

Extends BRD-001 §10 and BRD-002 §10; their terms continue to apply.

| Term | Meaning |
|---|---|
| **Understanding** | A distilled, self-contained unit of hard-won knowledge — the transferable skill that remains after the experience is discarded. Not a session log, a summary, or documentation of the code |
| **Load** | Injecting an Understanding export or foreign material into an agent's session context; non-destructive by default (does not write to the store) |
| **Import** | Capturing loaded material back into the store via the explicit `--store` switch, through the normal capture path |
| **Trigger** | The situation in which an Understanding applies, so a future agent recognises when to use it |
| **Provenance** | Where a piece of knowledge came from and when (BRD-001) — for an Understanding, the date and what it was learned from |

## 11. Related documents

| Document | Covers |
|---|---|
| [BRD 001 — Cross-product linked context memory](../001-context-memory/) | Capture, recall, trust and ownership (BR-01 … BR-17, BR-37) |
| [BRD 002 — Contextual knowledge export](../002-contextual-export/) | Export, composition and findings (BR-18 … BR-36) |
| [HLD 007 — Understanding transfer](../../hlds/007-understanding-transfer/) | The understanding kind, the load/import split, and the naming change |
| [HLD 005 — Contextual knowledge export](../../hlds/005-contextual-export/) | The contextual document an Understanding export may ride on |
| [HLD 006 — Corpus snapshot and restore](../../hlds/006-corpus-snapshot-and-restore/) | Durability of the store as one asset |
