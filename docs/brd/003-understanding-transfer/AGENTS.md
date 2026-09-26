# AGENTS.md — Understanding (BRD)

AI Context: BRD for understanding. Updated: 2026-09-26

## TL;DR

Business authority for making a distilled **Understanding** a first-class kind of stored knowledge, stored
in the **same store and models** as any memory, recalled with controllable breadth, and portable across
sessions; [README.md](./README.md) continues the shared requirement space at `BR-38` … `BR-45`, with
implementation owned by HLD 007.

## Non-Negotiables

- **Never add technology, data structures or implementation detail.** Same rule as BRD-001 and
  BRD-002. No endpoint shape, no schema, no library. Storage detail belongs in HLD 007.
- **Never renumber a `BR-NN`, and never restart numbering.** This BRD shares one requirement space with
  BRD-001 and BRD-002. It continues at `BR-38` because `BR-37` (durability) already sits in BRD-001.
- **Authority runs BRD → HLD, never back.** If HLD 007 or shipped behaviour contradicts a `BR-NN`, that
  is a business decision to escalate, not a doc-sync task.
- **This BRD extends BRD-001; it does not restate or supersede it.** Where a requirement here widens one
  there, it names it explicitly. Do not copy content in.
- **Memory is the correct name. It is not renamed.** Memory stays the scoped store of record. Understanding
  is a **kind** of memory, not a replacement term. Do not propose renaming the product vocabulary.
- **Never build a second persistence model or table for an Understanding.** It is a memory of
  `kind = understanding` (BR-40). A parallel path would split the write path.
- **Never weaken the model with nullables.** Cross-repo behaviour comes from default values and omitting
  the repository anchor, not from making scope nullable (BR-41).
- **Never soften the non-destructive default.** BR-43 (loading writes nothing) and BR-44 (import is
  opt-in via `--store`) are absolute.
- **Do not treat §5 out-of-scope rows as a backlog.** Notably, automatic capture without a switch was
  rejected on the grounds of the non-destructive default.

## System Context

BRD-001 governs capture and recall of scoped memory. This BRD governs the distilled Understanding — a
memory of `kind = understanding` that crosses repos and scopes through default values. HLD 007 owns the
design (one-model storage, load breadth split, portable export).

## Key Behaviors

- **`Accepted when:` clauses are the contract, not the prose above them.** The paragraph explains why;
  the clause is what a reviewer tests.
- **Understanding is not a session log and not documentation of code.** BR-38 is explicit. A design that
  stores a session transcript as an Understanding has missed the distinction.
- **Understanding is a kind of memory, so it gets the same write/version/recall path.** BR-40 is the
  point of the design. A proposal to give understanding its own table or entity contradicts it.
- **Cross-repo reach is by defaults, not nullables.** BR-41 requires a group carrying the default scope
  and no repo anchor. Do not read this as "make scope nullable" — the model stays strong.
- **Load and import are two acts.** BR-43 (load → context, no write) and BR-44 (import → store, opt-in)
  are separate. Do not conflate them.
- **Breadth is a load parameter, not two separate products.** `--all` vs understanding-only are the same
  operation with a different filter (BR-42).
- **§10 glossary: memory is unchanged.** Memory is the correct name; do not replace it.

## Migration Plans

- **HLD 007 must store understanding as a `kind`, not a separate artefact.** BR-40 is explicit. The HLD
  must not introduce a new entity, table or nullable scope.
- **HLD 005's no-import stance is amended for this capability only.** BRD-003 introduces an opt-in import
  path (BR-44). HLD 005's "a projection is never a source" remains true for the *dossier* path; the
  understanding import capability is a separate concern owned by HLD 007.
- **The `Memory`→`Understanding` code rename is not done.** Memory is the correct name; there is no
  rename to perform.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-26 | §8 assumption 1's closure path refined: a file-only understanding `load` records no recall feedback (the skill client makes no network call; feedback records only through `QueryMemories.Handler`), so the closure route names retrieval via the `mimisbrunnr-context-memory` query — which does hit `QueryMemories` — rather than mere capture-then-query. | BRD-003 §8 |
| 2026-09-26 | §8 wording tightened in two places, no outcome changed. The preamble said assumption 2 "was revised" while the table read "Validated, with a stated boundary" for it and "Open — carried forward" for assumption 1 — the preamble now matches both rows and names the `--all` arm as not yet run. The row-2 boundary also said "single observed instance ... not a pattern" without disclosing which breadth that instance was, so the `--all` comparative arm could read as covered; it now states the instance was understanding-only and `--all` is unrun. Both are restatements of evidence already in the rows, not new evidence. | BRD-003 §8 |
| 2026-09-26 | §8 row 2's closure route corrected. It named recall-feedback (HLD-004 NFR-03) as where this row's sustained validation accrues, but that signal observes the store's retrieval path, not the file-only `load` path the assumption is about — so the row could read as discharged by evidence that never exercises it. This row now needs repeated `load` runs at both breadths; recall-feedback is named as the indirect store-side signal, and as assumption 1's closure condition (where row 1 already assigns it). Supersedes the route wording in the row below, which recorded the dead-end half of the same fix. | BRD-003 §8 |
| 2026-09-26 | §8 row 2's boundary stated the single-instance limit but no longer named how sustained validation is gathered, so the row read as a dead end. The follow-up route is back: sustained validation accrues via recall-feedback (HLD-004 NFR-03) once understanding-kind memories are captured and repeatedly recalled — the same route assumption 1's closure condition names. | BRD-003 §8 |
| 2026-09-26 | §8 value assumption 2 (loading with controlled breadth is useful) moved from `Carried open` to **Validated, with a stated boundary**, on a single observed `load` run; assumption 1 stays `Carried open` with its stated closure condition. Supersedes the "value assumptions 1–2 carried open" wording in the row below. | BRD-003 §8 |
| 2026-09-26 | BRD status promoted `Draft` → `Approved`: `BR-38` … `BR-45` delivered by HLD 007 (all LADRs/NFRs `Accepted`, L1 `UnderstandingTransferStoreTests`). §8 assumption 3 validated; value assumptions 1–2 carried open with reason — they need usage evidence (recall feedback on understanding-kind memories, HLD-004) and cannot close before the capability is in use. | HLD-007; BRD-003 §8 |
| 2026-09-19 | Rebuilt for the one-model model: Understanding is a **kind** of memory (`kind = understanding`) stored in the existing models, not a separate artifact. Cross-repo reach via default scope and no repo anchor (no nullables). Added load-breadth (`--all` vs understanding-only) and the portable session export. Retracted the earlier vocabulary-rename requirement: memory is the correct name. `BR-45` is now the portable export, not a rename. | BRD-003 |
| 2026-09-19 | Created — BRD-003 for understanding. | BRD-003 |
