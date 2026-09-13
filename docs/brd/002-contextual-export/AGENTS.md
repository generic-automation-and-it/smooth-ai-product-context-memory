# AGENTS.md - Contextual knowledge export (BRD)

AI Context: BRD for contextual knowledge export. Updated: 2026-09-13

## TL;DR

Business authority for a portable grounding document that preserves product meaning and exposes its limits; [README.md](./README.md) continues BRD-001 at `BR-18` … `BR-34`, with implementation owned by HLD 005.

## Non-Negotiables

- **Never add technology, data structures or implementation detail.** Same rule as BRD-001. No endpoint shape, no schema, no library, no mention of the graph engine or the blob store. "Relationships between memories" is the business-level term; anything more specific belongs in HLD 005.
- **Never renumber a `BR-NN`, and never restart numbering at `BR-01`.** This BRD shares one requirement space with BRD-001 because it describes the same product. A second `BR-04` would make every citation ambiguous.
- **Authority runs BRD → HLD, never back.** If HLD 005 or the shipped behaviour contradicts a `BR-NN`, that is a business decision to escalate, not a doc-sync task.
- **This BRD extends BRD-001; it does not restate or supersede it.** Every constraint, risk and glossary term there still applies. Where a requirement here widens one there, it names it explicitly ("Extends BR-08 …"). Do not copy BRD-001 content in for convenience — two copies of one requirement drift.
- **HLD references follow BRD-001's three-place rule** (Related row, §11, the HLD's own AGENTS.md back-reference) for HLDs this BRD owns — currently HLD 005. §11 rows for sibling-owned HLDs (003, 004) are dependency pointers, not ownership listings.
- **The artefact is the only sharing mechanism, and that is a decision.** §4 states why: a file the practitioner reviews before sending is governed by their judgement; a shared query surface would need the access-control model BRD-001 exists without. Do not propose sharing, tenancy or multi-user access as a gap.
- **Never soften BR-30 or BR-33.** Silent omission and leaked hidden material are the two failures that make an export actively dangerous rather than merely incomplete. Both are absolute; neither has a "best effort" reading.
- **Do not treat §5 out-of-scope rows as a backlog.** Each was rejected on stated grounds — most importantly the import path, which would reverse the projection direction the whole design rests on.

## System Context

BRD-001 governs capture and recall; this BRD governs a composed document for spec preparation,
handover and re-entry. The existing whole-store dump already provides readable access to records;
contextual export adds composition and findings rather than replacing that path. HLD 005 owns its
design, with HLD 003 supplying relationships and HLD 004 supplying relevant findability evidence.

## Key Behaviors

- **`Accepted when:` clauses are the contract, not the prose above them.** The paragraph explains why; the clause is what a reviewer tests.
- **BR-19 and BR-30 are one idea seen twice.** Widening is only safe because its limits are reported. A design that widens well but reports its reach poorly fails both, and fails worse than one that under-reaches loudly.
- **BR-20 constrains selection only, never composition.** Selection must be reproducible; wording may vary between runs because composition is judgement. Reading BR-20 as "identical document" would forbid the judgement the capability exists to apply.
- **The snapshot date belongs to the document, not a claim's validity.** BR-21 dates the export; it does not certify that its claims are current or require generation time inside the deterministic selection described by HLD 005.
- **Compression and consolidation must preserve applicability.** BR-22 and BR-23 retain conditions, exceptions and all origins. Similar wording across customers or lifecycle states is not sufficient to merge; repeated captures of the same source are not necessarily independent evidence.
- **Proposed is not shipped.** BR-24 preserves recorded lifecycle and unknown status. Neither capture recency nor confident prose establishes current product behavior.
- **BR-25 distinguishes stored claims from generated analysis.** Questions and inferences need a stated basis, not a fabricated source memory. Headings and navigation are not substantive claims.
- **BR-27 bounds gap detection.** Its basis is the task, interpretation of included knowledge or an explicit practitioner expectation. General suggestions remain analysis; absence from a slice is not absence from the product or store.
- **BR-28 inherits BR-10's two-case structure.** Where a stated authority settles a disagreement, apply it and say so; where nothing does, the contradiction *is* the finding. Do not flatten these into "report everything" or "resolve everything".
- **BR-31 is why findings are output and not a write.** An export that records its own findings has turned a read into a capture and bypassed the judgement BR-01 depends on.
- **BR-32 is not a performance requirement.** It is about consent: the practitioner sees the size of what they asked for before paying for it. Making composition faster does not satisfy it.
- **§10 glossary is authoritative for this document's vocabulary.** It extends BRD-001 §10 and does not override it. Root `AGENTS.md` wins for implementation naming.

## Migration Plans

Design follow-up after review of the revised business requirements:

- **HLD 005 still describes the earlier gap definition as unresolved.** Align its gap findings with BR-27's stated basis. Do not implement generic expectations as established product rules. The HLD's analysis taxonomy and BR-25 attribution language also need to distinguish findings from stored claims.
- **HLD 005's collapse and lifecycle criteria need the fidelity cases in §7.** Preserve scope and proposed status, and do not count repeated copies of one source as independent corroboration. This BRD does not decide how those distinctions are stored.
- **Combined selection semantics and history policy need explicit design treatment.** BR-18 requires visible combination behavior without choosing an unstated AND/OR rule. BR-24 retains selected history, BR-30 accounts for limits, and BR-33 still excludes hidden material.
- **Preview consistency needs a design decision.** Define behavior when knowledge changes between preview and composition so the cost and scope reviewed by the practitioner remain meaningful (BR-20, BR-32).
- **Size and usability limits remain unmeasured.** Validate using the reference workflows before setting operational limits; do not assume a memory count guarantees a useful document or one-pass composition.
- **BR-29's overlap with HLD 004 remains open.** A bulk-reading hypothesis about a weak summary and an observed recall signal are different evidence. Keep that distinction when connecting the mechanisms.
- **Comparing exports over time is not required.** A dated snapshot and reproducible selection do not imply automatic refresh or document diffing.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-13 | Refined the PM grounding workflow, fidelity and lifecycle acceptance criteria, bounded gap findings, snapshot meaning and size preview; preserved BR-18 … BR-34 and single-user scope. Recorded downstream design alignment separately from business requirements. | BRD-002 §§4, 6–8 |
| 2026-09-13 | Created — BRD-002 for contextual knowledge export. Continues BRD-001's requirement space at `BR-18`; adds BO-6 … BO-10; closes BRD-001's recorded "BRD does not mention export at all" gap for the curated case. | HLD 005 |
| 2026-09-13 | Added the three-place HLD-reference rule (owned: HLD 005; §11 rows for 003/004 are dependency pointers). | BRD-001 |
