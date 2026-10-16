---
description: 'AGENTS.md quality standards, required structure, and anti-patterns'
globs: "**/*AGENTS.md"
paths:
  - "**/*AGENTS.md"
applyTo: '**/*AGENTS.md'
alwaysApply: true
---
# AGENTS.md Quality Standards

Rules for creating and updating `*AGENTS.md` files — including Phase 8 (Bragi) updates. Updated: 2026-10-07

## Purpose

AGENTS.md captures context source code cannot communicate: it prevents AI-coder mistakes, gives the "why" behind decisions, and marks system boundaries and constraints.

If information can be derived from source code or from the documents the file sits beside or points to, it does NOT belong in an AGENTS.md file. Every line must earn its place.

## Required Structure

Use exactly these sections, in this order. Omit any section (other than Changelog) that would be empty or "N/A". A design folder uses only items 1, 2, 7 and 9 — see *Design-Documentation Folders*.

1. **TL;DR** — One line. What this does and its most important constraint or behavior.
2. **Non-Negotiables** — Guardrails and forbidden patterns an AI coder would plausibly get wrong.
3. **System Context** — 2-4 sentences plus diagrams where applicable:

   **a) C4Context diagram** — External dependencies only; no internal components or data flows.
   - **Include for** services, workers and modules with external integrations (APIs, databases, queues, third parties); **omit for** components with none.
   - Mermaid `C4Context` with `System()` and `System_Ext()` nodes.

   **b) Sequence diagram** — Order of operations and side effects.
   - **Include for** flows with 3+ steps OR any side effect (email, SignalR, external API call, queue message); **omit for** simple CRUD, single steps, pure transformations.
   - Mermaid `sequenceDiagram`; participants as roles ("Handler", "SignalR Hub"), not class names; 5-10 steps — an overview, not a trace.

   **c) ER diagram** — Entity relationships for data access.
   - **Include for** code operating on 3+ related entities with non-obvious relationships; **omit for** single-entity CRUD or relationships obvious from naming (Order → OrderProduct).
   - Mermaid `erDiagram`; a per-domain cluster of 5-10 entities, focused on the relationships needed to write correct queries.

   One AGENTS.md may carry several diagram types.
4. **Architecture Decisions** *(code folders only — a design folder's decisions live in its `ladrs/`)* — LADR format (LADR-NNN, Date, Status, Context, Decision, Consequences). Only decisions whose rejected alternative would look reasonable to an AI coder: it has real trade-offs, the reasoning isn't obvious from code, or getting it wrong has non-obvious consequences (production failures, data corruption, silent sync issues).
5. **Key Behaviors** — Non-obvious behaviors, edge cases, cross-cutting concerns NOT apparent from source code.
6. **Test References** — Test tier (L0/L1/L2, as defined in root `AGENTS.md`) and sub-folder path within test projects. Updated when tests are added or modified.
7. **Quality Constraints** — Feature-specific non-functional requirements beyond root `AGENTS.md`, `.docs/hlds/` NFRs and the rule files, and only those that change how code is written.
8. **Migration Plans** — Planned migrations, deprecations or debt affecting new code: what changes, the target state, what not to build on.
9. **Changelog** — Always include the header, even if empty: `| Date | Change | Ref |`; Ref is an issue/PR (`#88`) or `—`. A row is the shortest text an agent can still act on:
   - **Fragment, not sentence.** What changed in the guidance, plus IDs. No rationale, no narration of the authoring process, no restating the file's content, no counts that go stale ("21 open questions").
   - **One row per change that alters agent guidance.** Same-day rows for one change merge into one. Keep bug/gotcha/pitfall rows.
   - **A row earns extra words only to warn an agent** — e.g. that old IDs are void.
   - ✅ `| 2026-10-07 | Cut to non-HLD content; README pointer | — |` · `| 2026-10-07 | HLD rewritten: one LADR, OQ-1..4; older IDs void | — |` · `| 2026-10-06 | Scaffolded (LADR-01..04) | — |`
   - ❌ `| 2026-10-06 | Added HLD-002 with three LADRs covering persistence, caching and retries, plus 14 open questions |` — a multi-clause HLD summary, stale the moment the HLD changes.

## Value Gate

Apply per claim. **Every gate must pass**; a line failing any one is deleted or cut to the part that passes.

1. **Counterfactual.** Name the concrete wrong code or design choice an AI coder makes without this line. If you cannot name one, delete the line.
2. **Local delta.** The prevention is not already stated in anything the reader has loaded or is pointed to: root `AGENTS.md`, the parent `AGENTS.md`, matching rule files, ADRs, the source code — and, in a design folder, its README, LADRs, NFRs and diagrams. If it is stated there, delete the line, or keep only the local exception or an ID reference.
3. **Authority.** The claim is settled and current. An open question appears only when it blocks a likely premature implementation that its owning document does not already block — by ID, one line.
4. **Single home.** The fact lives in exactly one file. A line that also appears in a sibling `AGENTS.md` moves to their nearest common parent on its second occurrence; if one fact changing would force edits in two files, it is duplicated.

Gate 1 alone keeps every restated LADR. Gates 2 and 4 are [clean-code](../clean-code.instructions.md)'s *extract on the second occurrence* and [SOLID](../solid-principles.instructions.md)'s *change cost*, applied to context.

## Design-Documentation Folders

Applies to `.docs/hlds/NNN-*/AGENTS.md` (authored by the `create-hld` skill). The HLD — README, LADRs, NFRs, diagrams — is written for humans **and** AI coders; its AGENTS.md holds only agent-relevant content found in **none** of them: a tie to a repo rule the HLD does not mention, a constraint from a source document the HLD omits, a vocabulary trap the HLD does not define.

- **TL;DR is one line pointing at `README.md`** — agents auto-load AGENTS.md, not README.md. The only pointer line; any other link sits inside the Non-Negotiable that needs it.
- **Omit** System Context (diagrams live in `diagrams/`), Architecture Decisions (the README LADR table and `ladrs/` are the home), Key Behaviors, Test References and Migration Plans (nothing runs yet; rollout is the tracker's). Omit Quality Constraints unless it states something no NFR does.
- **A Non-Negotiable names the constraint and its source** (the rule, the brief) — a mechanism only when the wrong one is the trap. An uncitable constraint fails gate 3.
- **Draft and Prototype LADRs bind as intent**: flag a deviation, never silently override it. Stated here once — do not repeat it per HLD.
- **A TL;DR, at most one or two Non-Negotiables and a Changelog is the expected result**, not an incomplete one. Zero Non-Negotiables is valid.

Example (`.docs/hlds/001-pallet-allocation/AGENTS.md` — shape only; do not copy its trap into an HLD that lacks it):

```markdown
# AGENTS.md - Pallet Allocation

## TL;DR

Read [`README.md`](./README.md) first — the allocation design, its LADRs and its NFRs live there.

## Non-Negotiables

- **"Allocation service" is the README's domain term, not a class shape.** Build it as an Application
  feature slice per [architecture-slices](../../../.agents/rules/backend/architecture-slices.instructions.md),
  never as an `AllocationService` layer.

## Changelog
...
```

A version restating LADR-01, the NFR targets or the README goals as bullets fails gate 2 on each of them.

## Anti-Patterns (MUST avoid)

- **No `(src: path)` annotations**: The AI can find files itself.
- **No file listings as "components"**: The AI can glob/grep for files.
- **No restating code, or a design folder's README, LADRs or NFRs** — including as a summary table or as Non-Negotiables recapping LADRs.
- **No generic boilerplate**: "Required secrets: AWS_ACCESS_KEY_ID" adds no value.
- **No folder-layout preambles or `AI Context: … Updated:` lines** ("diagrams live in …, guardrails not narrative"): the folder is self-evident and the date is in the Changelog.
- **No root policy wrapped around a local list** ("do not resolve an open question by inference. In particular: …"): the policy is root's; keep only a trap the owning document does not already block.
- **No business value / problem-solution-impact blocks**: Human product context, not AI coding context.
- **No validation checklists that restate expected behavior**.
- **No "see other file" cross-references as section content**: A single line in TL;DR or Key Behaviors suffices.
- **No cosmetic or dead-reference Changelog rows**: ID renumbering, "aligned with", or a row citing an ID that no longer exists — an agent grepping the ID lands on a dead reference.

## Drift Minimization

A code or design change MUST update the `*AGENTS.md` sections it makes untrue.

- **Update obligation**: A change to behaviour documented in an AGENTS.md (Key Behaviors, Architecture Decisions, diagrams) updates it in the same commit or PR.
- **Diagram accuracy**: Diagrams reflect current state; a new external dependency, handler step or entity relationship updates the matching diagram.
- **Changelog tracking**: Every update that alters agent guidance adds one row in the *Required Structure* item 9 format.
- **No stale documentation**: Stale context is worse than none. Drift noticed during Phase 0 (Context Load) is fixed before proceeding.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change |
|:-----|:-------|
| 2026-05-30 | Initial version. |
| 2026-10-07 | Conjunctive value gate; design-folder (HLD) section; terse changelog rows; tiers L0–L2 |
