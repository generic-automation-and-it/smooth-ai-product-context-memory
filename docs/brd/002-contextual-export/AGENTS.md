# AGENTS.md - Contextual knowledge export (BRD)

AI Context: BRD for contextual knowledge export. Updated: 2026-09-13

## TL;DR

Business authority for exporting a **slice** of the store as one composed document plus a findings
report. Requirements in [README.md](./README.md), numbered `BR-18` … `BR-34`, continuing
[BRD-001](../001-context-memory/)'s space. Technology-free by construction — every "how" lives in
[HLD 005](../../hlds/005-contextual-export/).

## Non-Negotiables

- **Never add technology, data structures or implementation detail.** Same rule as BRD-001. No endpoint shape, no schema, no library, no mention of the graph engine or the blob store. "Relationships between memories" is the business-level term; anything more specific belongs in HLD 005.
- **Never renumber a `BR-NN`, and never restart numbering at `BR-01`.** This BRD shares one requirement space with BRD-001 because it describes the same product. A second `BR-04` would make every citation ambiguous.
- **Authority runs BRD → HLD, never back.** If HLD 005 or the shipped behaviour contradicts a `BR-NN`, that is a business decision to escalate, not a doc-sync task.
- **This BRD extends BRD-001; it does not restate or supersede it.** Every constraint, risk and glossary term there still applies. Where a requirement here widens one there, it names it explicitly ("Extends BR-08 …"). Do not copy BRD-001 content in for convenience — two copies of one requirement drift.
- **The artefact is the only sharing mechanism, and that is a decision.** §4 states why: a file the practitioner reviews before sending is governed by their judgement; a shared query surface would need the access-control model BRD-001 exists without. Do not propose sharing, tenancy or multi-user access as a gap.
- **Never soften BR-30 or BR-33.** Silent omission and leaked hidden material are the two failures that make an export actively dangerous rather than merely incomplete. Both are absolute; neither has a "best effort" reading.
- **Do not treat §5 out-of-scope rows as a backlog.** Each was rejected on stated grounds — most importantly the import path, which would reverse the projection direction the whole design rests on.

## System Context

BRD-001 covers capture and recall — knowledge going in, and answers coming out one question at a time.
This BRD covers the third movement: knowledge coming out **in bulk, as a document**. It exists because
BRD-001's narrowness (BR-06) is correct for answering and insufficient for understanding, and because
its trust requirements (BR-10 contradictions, BR-15 gaps) only fire when a retrieval happens to touch
the problem.

One HLD sits below it — [HLD 005](../../hlds/005-contextual-export/). Two others are load-bearing
dependencies rather than children: HLD 003 supplies the relationship traversal BR-19 needs, and HLD 004
overlaps BR-29's weak-summary finding.

This BRD also closes a gap recorded against BRD-001: its `AGENTS.md` notes that "BR-17 is stated as
settled and is not … the BRD does not mention export at all." BR-21 here is the curated half of that
answer. The whole-store dump is the other half and predates this document.

## Key Behaviors

- **`Accepted when:` clauses are the contract, not the prose above them.** The paragraph explains why; the clause is what a reviewer tests.
- **BR-19 and BR-30 are one idea seen twice.** Widening is only safe because its limits are reported. A design that widens well but reports its reach poorly fails both, and fails worse than one that under-reaches loudly.
- **BR-20 constrains selection only, never composition.** Selection must be reproducible; wording may vary between runs because composition is judgement. Reading BR-20 as "identical document" would forbid the judgement the capability exists to apply.
- **BR-23 is collapse, not deduplication.** Every origin survives the merge. A design that picks a winner and discards the rest satisfies the word "once" and breaks the requirement.
- **BR-28 inherits BR-10's two-case structure.** Where a stated authority settles a disagreement, apply it and say so; where nothing does, the contradiction *is* the finding. Do not flatten these into "report everything" or "resolve everything".
- **BR-31 is why findings are output and not a write.** An export that records its own findings has turned a read into a capture and bypassed the judgement BR-01 depends on.
- **BR-32 is not a performance requirement.** It is about consent: the practitioner sees the size of what they asked for before paying for it. Making composition faster does not satisfy it.
- **§10 glossary is authoritative for this document's vocabulary.** It extends BRD-001 §10 and does not override it. Root `AGENTS.md` wins for implementation naming.

## Migration Plans

Known incompleteness as of 2026-09-13, deliberately left open rather than guessed at:

- **"What a slice should reasonably contain" is undefined** (BR-27). Gap detection needs an expectation to measure against, and the BRD asserts one exists without stating where it comes from. Until that is settled, gap findings will be whatever the composing model considers conspicuous — useful, but not a specification. Resolve when the first exports show which absences actually matter.
- **No requirement governs export age.** A document is a projection at a moment, and nothing says whether a three-month-old export handed to a colleague should announce that it is stale. Trigger: the first time an export is re-read rather than regenerated.
- **The slice-size assumption (§8, tens to low hundreds) is unmeasured.** Every cost and readability judgement rests on it. Trigger: the first export whose slice exceeds it.
- **BR-29's overlap with HLD 004 is unreconciled.** Both address weak summaries — HLD 004 from never-recalled signal, this from bulk reading. Two mechanisms answering one question will eventually disagree about which memories are weak. Resolve when HLD 004 leaves discovery.
- **Nothing requires exports to be comparable over time.** BR-20 makes selection reproducible, which is not the same as making two exports of the same slice diffable across a month of capture. Whether that matters is unknown.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-13 | Created — BRD-002 for contextual knowledge export. Continues BRD-001's requirement space at `BR-18`; adds BO-6 … BO-10; closes BRD-001's recorded "BRD does not mention export at all" gap for the curated case. | HLD 005 |
