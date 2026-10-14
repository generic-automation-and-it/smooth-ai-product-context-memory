# BRD-004: Mímisbrunnr Console (Viewer & Management UI)

| | |
|---|---|
| **Document** | Business Requirements Document |
| **Status** | In Draft |
| **Owner** | Product owner / practitioner |
| **Last updated** | 2026-10-13 |
| **Extends** | [BRD 001 — Cross-product linked context memory](../001-context-memory/) |
| **Related** | [HLD 009 — Viewer console](../../hlds/009-viewer-console/) |

> This document states **what the business needs and why**. Technology choices, data structures
> and implementation belong in the HLDs. It extends BRD-001's requirement space at `BR-46` onwards.
> BRD-001, BRD-002 and BRD-003 continue to apply.

---

## 1. Executive summary

The store keeps scoped memory and distilled Understandings for AI agents. Every consumer today is an
agent, a CLI, or the raw OpenAPI surface. The practitioner — the human whose judgment the whole design
is built around — has **no way to see the corpus they are building**.

The Console is a loopback human-facing surface on top of the existing HTTP API. It lets the practitioner
**browse and filter** what is saved, **read** a memory's full detail and provenance, **see the graph**
of typed edges and ticket paths, and **apply judgment on the write path** — reviewing the capture
queue, resolving conflicts, and approving a proposal — without bypassing any of the gates the write
path already enforces.

It is deliberately **read-capability first and write-judgement second**, because the two halves are
governed differently. Reading is ambient and cheap; writing is gated, client-side and carries a human
veto. The Console exposes the second half **through the existing capture pipeline**, never by
reimplementing its redaction, atomicity or dedup logic.

## 2. Business problem

| Problem | Cost to the practitioner |
|---|---|
| **The corpus is invisible** | The store accumulates, but the practitioner cannot see what is there, how much, or whether it is noise |
| **Capture is approved blind** | The review queue and conflict menus live in a terminal flow; the practitioner cannot see the surrounding context while judging |
| **Recall correctness is unverifiable** | There is no surface to confirm a memory reads correctly, is current, or cites its sources |
| **The graph is abstract** | Typed edges and ticket paths exist in the data but no human has a view of them |
| **Never-recalled knowledge is hidden** | The recall-feedback surfaces exist but only through a CLI; the practitioner cannot see what is never returning |

## 3. Business objectives

| # | Objective | Why it matters |
|---|---|---|
| **BO-21** | Make the corpus visible and browsable | The practitioner can inspect what is saved, filter it, and judge capture quality |
| **BO-22** | Show a memory's detail and provenance | Trust in a recall requires seeing the source, version history and validity window behind it |
| **BO-23** | Visualise the graph | Typed edges and ticket paths become understandable to a human, not just queryable |
| **BO-24** | Apply human judgment on the write path through the existing pipeline | The dry-run veto and review queue stay authoritative; the UI presents them, never replaces them |
| **BO-25** | Surface recall quality | Never-recalled and miss-rate are visible, so capture vs recall tuning is evidence-driven |
| **BO-26** | Keep the single-user, loopback, capability-split boundary | The Console adds a human surface without adding a multi-user model or making writes ambient |

BO-21 … BO-25 extend BRD-001's BO-1 … BO-5 and BRD-003's BO-11 … BO-15. BO-24 is the tightest
constraint: it reuses the capture path's gates rather than re-encoding them.

## 4. Users and stakeholders

| Party | Interest |
|---|---|
| **Primary user — the practitioner** | Browses the corpus, reads detail, sees the graph, reviews and approves captures |
| **The AI assistant (indirect)** | Captures it proposes are judged by a human with full context; the record improves |

BRD-001's single-user boundary is unchanged. The Console is loopback-only and runs on the same machine
as the store. There is no multi-user, no authorization model beyond the existing capability split.

### Reference workflow: reviewing a pending capture with context

1. The capture path proposes a memory and pauses at the review queue.
2. The practitioner opens the Console and sees the proposal in the queue, with the surrounding
   memories it touches and any conflict choices the dedup surfaced.
3. They inspect the detail and provenance of the related memories.
4. They accept, amend, or reject. Accepting re-runs the same gate sequence (redaction → atomicity →
   dedup → `set --dryrun` → write); rejecting records the decision without writing.

## 5. Scope

### In scope

- **Browse + filter** — list and filter every saved memory / understanding by ticket, initiative,
  repo, facet, tag, kind, status, group, free text, and the validity window.
- **Detail** — read a memory's statement, summary, sources, version history, validity, confidence,
  scope, and blob bodies.
- **Graph** — force-directed view of the typed edges and ticket paths, with bounded traversal.
- **Management** — surface the capture review queue, conflict-choice menus, propose/approve, upsert
  labels and initiatives, set ticket parent; all through the existing gates.
- **Recall feedback** — surface never-recalled and miss-rate so tuning is evidence-driven.
- **Loopback entry** — a single local URL, capability-split credentials, write token never ambient.

### Out of scope

| Excluded | Reason |
|---|---|
| Automatic or background capture | BR-11 checkpoint and the human veto; capture stays deliberate |
| Edit or delete of a memory | Append-only versions; a correction is a version bump, not an edit |
| A model inside the Console or the bridge | The no-model-in-Application/Host rule (HLD-005 §2.6); judgement stays in the skill |
| Reimplementing the capture gates in the browser | Two sources of truth for redaction/atomicity/dedup drift; refused (BRD-004 HLD-009 LADR) |
| Multi-user or a team corpus | BRD-001 single-user boundary is unchanged |
| Vector retrieval | Only reopened on recall-feedback evidence, not because a UI exists |

## 6. Business requirements

### Browse and filter

**BR-46 — The practitioner must be able to browse the saved corpus.**
Every memory and understanding in scope (per the read capability and scope rule) is listable.

*Accepted when:* the Console lists saved elements with cheap fields (name, statement/summary, kind,
facets, tags, status, confidence, scope, validity, version), and a human can scroll the full result set
subject to the retrieval limit.

**BR-47 — The browse view must support the store's declared filters.**
The filter surface maps one-for-one onto the query API's declared fields: ticket (provider + key),
initiative, repo, facet (with any/all), tag, kind, status, group, free text, and the validity window.

*Accepted when:* a filter on each declared field narrows the list exactly as the query endpoint narrows
it, and facet any/all is a visible choice rather than an implicit default.

### Detail and provenance

**BR-48 — Selecting an element must reveal its full detail and provenance.**
The practitioner can read the statement, summary, sources, version history, validity window,
confidence, scope, and blob body of a memory.

*Accepted when:* selecting an element renders those fields, with blob bodies fetched on demand, and a
version history that shows current vs superseded versions.

**BR-49 — Recalled material must be framed as evidence, not instructions.**
Whatever the Console renders from the store is presented as cited historical data, consistent with the
read path's untrusted-data framing.

*Accepted when:* a rendered memory carries a visible provenance/notice frame and its source identity,
matching the RECALL_NOTICE convention on the read path.

### Graph

**BR-50 — The practitioner must be able to visualise the graph.**
Typed edges (links) and ticket parent/child paths are rendered as a force-directed view, with bounded
traversal depth matching the store's constraints.

*Accepted when:* a selected element renders its connected neighbours and edges, the edge types are
distinguishable and labelled, and the traversal respects the store's depth bound (not an unbounded
walk).

### Management

**BR-51 — The capture review queue and conflict choices must be visible and actionable.**
The practitioner can review a pending proposal, see its context, resolve a conflict the dedup surfaced,
and approve or reject — without bypassing the capture gates.

*Accepted when:* a proposal in the queue is viewable with its surrounding memory context, a conflict
choice is presented as the same choice the capture path presents, and approving re-runs the gate
sequence (including `set --dryrun`) rather than writing directly.

**BR-52 — Write capability must never be ambient.**
The Console may hold the write token server-side for an approved capture, but the browser never carries
it, and no write occurs without the human approval step.

*Accepted when:* the browser holds only the read token (or none, when Caddy injects it), a capture is
proposed read-only, and the write token is exercised only on an explicit approve.

### Recall feedback

**BR-53 — Recall quality must be visible.**
Never-recalled and miss-rate are surfaced so the practitioner can tune capture vs recall with evidence.

*Accepted when:* the Console renders the never-recalled and miss-rate surfaces, and the miss-vs-absence
distinction is shown rather than collapsed.

## 7. Success measures

| Measure | Acceptance or evaluation |
|---|---|
| **Corpus visibility** | The practitioner can list and filter the whole corpus from a browser and confirm what is saved |
| **Provenance trust** | A memory's sources, version history and validity are readable; recalled material is framed as evidence |
| **Graph legibility** | Typed edges and ticket paths are distinguishable and bounded in a rendered view |
| **Gate integrity** | Approving a capture re-runs the gate sequence; the Console changes none of the capture gates |
| **Write non-ambience** | The browser never holds a write token; no write without an explicit approve |
| **Evidence-driven tuning** | Never-recalled and miss-rate are visible, so tuning uses real data not a competitor's feature list |

## 8. Constraints and assumptions

### Constraints

- All BRD-001, BRD-002 and BRD-003 constraints continue to apply, including the single-user boundary.
- The Console is loopback-only and does not add a multi-user authorization model (BO-26).
- It reuses the capture path's gates; it does not reimplement redaction, atomicity or dedup (BR-51).
- There is no model inside the Console or its bridge (HLD-005 §2.6).
- The graph traversal is bounded to the store's declared depth (BR-50); it is not a graph database.

### Assumptions to validate

| Assumption | Outcome |
|---|---|
| A human-facing browse surface is genuinely useful for a single-user local store. | **To validate.** The store is agent-first; usage is the measure. The Console is deliberately cheap to adopt (read-first) so the value is observable before the write half is built. |
| The review queue can be surfaced without breaking its client-side veto. | **To validate.** Depends on proposals being persisted with `Status = proposed` (so a query can derive the queue) and on the bridge reproducing conflict choices rather than re-encoding them (HLD-009 G1, G2, LADR-10). |
| A graph view adds value over the existing `/paths` JSON for a shallow bounded graph. | **To validate.** The graph is additive, not the store's home (HLD-003); the value may be modest at small corpus sizes. |

## 9. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| **The Console bypasses a gate** | A capture written without redaction/atomicity/dedup | The bridge drives the existing capture pipeline; the write token is not ambient (BR-51, BR-52) |
| **The write path diverges** | Two sources of truth for gates drift | Reuse the capture scripts; refuse browser reimplementation |
| **A second entry point weakens the loopback boundary** | The local store is exposed to the network | Only a loopback bind; Caddy or the bridge binds localhost only |
| **The UI is surface over little depth** | Effort spent where the corpus is small | Read-first phasing; value measured before the write half |
| **The graph view over-promises** | A shallow graph shown as a full knowledge graph | Render only the typed edges and bounded traversal the store holds; state the depth |

## 10. Glossary

Extends BRD-001 §10, BRD-002 §10 and BRD-003 §10; their terms continue to apply.

| Term | Meaning |
|---|---|
| **Console** | The loopback human-facing surface described by BRD-004 (browse, detail, graph, management, recall feedback) |
| **Viewer project** | The UI and its bridge, deployed as a separate pod fronted by Caddy |
| **Bridge** | The thin component in the viewer pod that drives the existing capture pipeline rather than reimplementing it |
| **Review queue** | The capture path's pending proposals, with conflict choices and a human veto |
| **Capability split** | BRD-001's read/write token separation; read ambient, write never ambient |

## 11. Related documents

| Document | Covers |
|---|---|
| [BRD 001 — Cross-product linked context memory](../001-context-memory/) | Capture, recall, trust and ownership (BR-01 … BR-17, BR-37) |
| [BRD 003 — Understanding](../003-understanding-transfer/) | The understanding kind and cross-repo reach (BR-38 … BR-45) |
| [HLD 009 — Viewer console](../../hlds/009-viewer-console/) | The separate-pod architecture, the write-path bridge, the data views, and the UX |
| [HLD 003 — Graph edges on AGE](../../hlds/003-graph-edges-on-age/) | The typed edges and bounded traversal the graph view renders |
| [HLD 004 — Memory recall feedback](../../hlds/004-memory-recall-feedback/) | The never-recalled and miss-rate surfaces the Console surfaces |
