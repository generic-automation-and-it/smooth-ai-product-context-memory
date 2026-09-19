# AGENTS.md - Contextual knowledge export (BRD)

AI Context: BRD for contextual knowledge export. Updated: 2026-09-14

## TL;DR

Business authority for a portable grounding document that preserves product meaning and exposes its limits; [README.md](./README.md) continues BRD-001 at `BR-18` … `BR-36`, with implementation owned by HLD 005.

## Non-Negotiables

- **Never add technology, data structures or implementation detail.** Same rule as BRD-001. No endpoint shape, no schema, no library, no mention of the graph engine or the blob store. "Relationships between memories" is the business-level term; anything more specific belongs in HLD 005.
- **Never renumber a `BR-NN`, and never restart numbering at `BR-01`.** This BRD shares one requirement space with BRD-001 because it describes the same product. A second `BR-04` would make every citation ambiguous.
- **Authority runs BRD → HLD, never back.** If HLD 005 or the shipped behaviour contradicts a `BR-NN`, that is a business decision to escalate, not a doc-sync task.
- **This BRD extends BRD-001; it does not restate or supersede it.** Every constraint, risk and glossary term there still applies. Where a requirement here widens one there, it names it explicitly ("Extends BR-08 …"). Do not copy BRD-001 content in for convenience — two copies of one requirement drift.
- **HLD references follow BRD-001's three-place rule** (Related row, §11, the HLD's own AGENTS.md back-reference) for HLDs this BRD owns — currently HLD 005. §11 rows for sibling-owned HLDs (003, 004) are dependency pointers, not ownership listings.
- **The artefact is the only sharing mechanism, and that is a decision.** §4 states why: a file the practitioner reviews before sending is governed by their judgement; a shared query surface would need the access-control model BRD-001 exists without. Do not propose sharing, tenancy or multi-user access as a gap.
- **Never soften BR-30, BR-33 or BR-36.** Silent omission, leaked hidden material, and a focus that narrows selection are the three failures that make an export actively dangerous rather than merely incomplete. All three are absolute; none has a "best effort" reading.
- **Never treat a focus as a filter.** BR-35 is emphasis; BR-36 forbids it changing what was selected or hiding a finding. A focus implemented as a selection predicate looks like the obvious reading and destroys comparability between two focuses of one slice.
- **Do not treat §5 out-of-scope rows as a backlog.** Each was rejected on stated grounds — most importantly the import path, which would reverse the projection direction the whole design rests on. *(Exception: the understanding load/import and current-session dump are a separate capability owned by [BRD 003](../003-understanding-transfer/), so they do not reopen this row for the contextual document; the dossier itself stays one-way.)*

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
- **BR-35 names the work, not the reader.** §4 states why: who reads an export varies by occasion and is unknowable at request time, while the artefact the reader is about to produce is knowable. A proposal to key focuses on audience ("for a colleague", "for an agent") reverses that and should be rejected.
- **The unfocused document is the default and is not a degraded case.** A complete dump is a first-class, frequent need. A design that requires a focus has broken BR-35's own acceptance clause.
- **§10 glossary is authoritative for this document's vocabulary.** It extends BRD-001 §10 and does not override it. Root `AGENTS.md` wins for implementation naming.

## Migration Plans

Design follow-up after review of the revised business requirements:

- **HLD 005's gap definition is aligned (closed 2026-09-14).** LADR-13 implements BR-27's three grounds and NFR-05 labels analysis with its basis — do not reintroduce a generic-expectations rule.
- **HLD 005's consolidation and lifecycle criteria carry the fidelity cases (closed 2026-09-14).** LADR-05 requires equivalence of meaning, applicability and lifecycle, and repeated captures of one source are not corroboration. This BRD does not decide how those distinctions are stored.
- **Combined selection semantics and history policy are designed (closed 2026-09-14).** LADR-03 states the combination rule (alternatives within a category, conjunctive across categories), keeps history a selection dimension priced by the preview, and never silently broadens a no-match.
- **Preview consistency is decided (closed 2026-09-14).** LADR-14 binds preview and composition to one recorded selection — the scope the practitioner approved is the scope composed, or the difference is reported (BR-20, BR-32).
- **Size and usability limits remain unmeasured.** Validate using the reference workflows before setting operational limits; do not assume a memory count guarantees a useful document or one-pass composition.
- **BR-29's overlap with HLD 004 remains open.** A bulk-reading hypothesis about a weak summary and an observed recall signal are different evidence. Keep that distinction when connecting the mechanisms.
- **Comparing exports over time is not required.** A dated snapshot and reproducible selection do not imply automatic refresh or document diffing.
- **No requirement governs export age.** A document is a projection at a moment, and nothing says whether a three-month-old export handed to a colleague should announce that it is stale. Trigger: the first time an export is re-read rather than regenerated.
- **The focus set is stated but not yet validated** (BR-35). Five were named from how the practitioner actually works; only use will show whether *specification* is distinct enough from *architecture* to survive, or whether *review* is a focus at all rather than a different document shape. Trigger: the first focused exports. Do not add a sixth before the five have been used.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-19 | Scoped the no-import stance to the contextual document and cross-referenced BRD 003 (understanding load/import + current-session export, BR-38…BR-45) in §5 out-of-scope, §10 glossary and §11 Related. The dossier stays one-way. | BRD-003 |
| 2026-09-13 | Refined the PM grounding workflow, fidelity and lifecycle acceptance criteria, bounded gap findings, snapshot meaning and size preview; preserved BR-18 … BR-34 and single-user scope. Recorded downstream design alignment separately from business requirements. | BRD-002 §§4, 6–8 |
| 2026-09-13 | Created — BRD-002 for contextual knowledge export. Continues BRD-001's requirement space at `BR-18`; adds BO-6 … BO-10; closes BRD-001's recorded "BRD does not mention export at all" gap for the curated case. | HLD 005 |
| 2026-09-13 | Added the three-place HLD-reference rule (owned: HLD 005; §11 rows for 003/004 are dependency pointers). | BRD-001 |
| 2026-09-13 | Added BR-35 (focus on the work the document feeds) and BR-36 (a focus never changes selection and never suppresses a finding), plus §4's statement that there is deliberately no single primary consumer. Raised by review asking who the primary user is; the answer is that the work is knowable at request time and the reader is not. | BR-35, BR-36 |
| 2026-09-14 | Migration Plans: HLD 005 alignment items — gap definition, consolidation and lifecycle criteria, combination semantics, preview consistency — marked closed; delivered by HLD 005's 2026-09-14 revision (LADR-13, LADR-14, revised LADR-03/LADR-05, NFR-05, NFR-07). | HLD 005 |
