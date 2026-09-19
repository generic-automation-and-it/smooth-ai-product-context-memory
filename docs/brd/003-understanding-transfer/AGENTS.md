# AGENTS.md — Understanding transfer and loading (BRD)

AI Context: BRD for understanding transfer and loading. Updated: 2026-09-19

## TL;DR

Business authority for making a distilled **Understanding** a first-class, portable unit and for
loading it (or any foreign material) into an agent's context; [README.md](./README.md) continues the
shared requirement space at `BR-38` … `BR-46`, with implementation owned by HLD 007.

## Non-Negotiables

- **Never add technology, data structures or implementation detail.** Same rule as BRD-001 and
  BRD-002. No endpoint shape, no schema, no library. Understanding's five-part shape and the
  load/import split are business-level here; storage detail belongs in HLD 007.
- **Never renumber a `BR-NN`, and never restart numbering.** This BRD shares one requirement space
  with BRD-001 and BRD-002. It continues at `BR-38` because BR-37 (durability) already sits in
  BRD-001. Do not renumber to fit a section.
- **Authority runs BRD → HLD, never back.** If HLD 007 or shipped behaviour contradicts a `BR-NN`,
  that is a business decision to escalate, not a doc-sync task.
- **This BRD extends BRD-001 and BRD-002; it does not restate or supersede them.** Where a
  requirement here widens or amends one there, it names it explicitly. Do not copy content in.
- **Never soften the non-destructive default.** BR-41 (load writes nothing) and BR-43 (import is
  opt-in via `--store`) are absolute. There is no "best effort" reading of "loading does not write".
- **Never let import bypass the capture path's judgement.** Imported material always passes through
  atomicity, redaction, deduplication and link derivation (BR-43). A direct write is a defect.
- **Never treat `--store` as a hint or a convenience.** It is the switch that makes a read into a
  capture. Omitting it is the documented, safe default.
- **Do not treat §5 out-of-scope rows as a backlog.** Notably, automatic capture without a switch was
  rejected on the grounds of BO-14, and the store's internal memory model is not broadened.
- **Never let the naming change (BR-45) break the wire.** "Understanding" is product prose, the
  glossary and user-facing terms. Code identifiers, database tables and the API/MCP wire contract
  keep their existing names. A code rename that touches the public contract is out of scope here.
- **Never treat the session dump (BR-46) as a write.** Dumping the current session's context to
  `.context/understandings/` is an *export* — it changes nothing in the store. It is the one place the
  skill writes to a file, and that file is a projection, never a source.

## System Context

BRD-001 governs capture and recall, BRD-002 governs a composed document for export. This BRD governs
the transfer of a distilled unit and the loading of material into a fresh agent's context. HLD 007
owns the design (understanding kind, load/import split, selectors, naming), with HLD 005 supplying the
contextual document an understanding export may ride on.

## Key Behaviors

- **`Accepted when:` clauses are the contract, not the prose above them.** The paragraph explains
  why; the clause is what a reviewer tests.
- **Understanding is not a session log and not documentation of code.** BR-38 is explicit. A design
  that stores a session transcript as an Understanding has missed the distinction — the Understanding
  is the *transferable skill that remains after the experience is discarded*.
- **Load and import are two acts, not one.** BR-41 (load → context, no write) and BR-43 (import →
  store, opt-in) are separate. Do not conflate them into a single "ingest" operation.
- **BR-42 narrows what "load" means for foreign input.** Loading is about making the material usable
  as grounding context, not about changing the store's model. A proposal to add a session/meeting
  artefact type to the store should be checked against §5 out-of-scope.
- **BR-44's selectors are about association, not about selection for reading.** `--tickets`/`--tags`
  bind imported material to work; they do not filter what the agent reads.
- **BR-45 is a vocabulary change, not a structural one.** It must never drive a breaking rename of
  the public API, DB tables or MCP tool names.
- **§10 glossary extends, it does not override.** BRD-001 §10 and BRD-002 §10 continue to apply; this
  BRD adds the Understanding-specific terms.

## Migration Plans

Design follow-up for HLD 007:

- **The Understanding five-part shape must be mapped onto stored fields by the HLD.** BR-38 states
  the five parts (trigger, knowledge, why, boundaries, provenance). Whether they map onto existing
  memory fields or require a new field is a design decision, not a business one. The HLD must decide
  and must not violate the "no 7th entity / no schema change" guardrail unless justified.
- **HLD 005's no-import stance is amended for this capability only.** BRD-003 introduces an opt-in
  import path (BR-43). HLD 005's "a projection is never a source" remains true for the *dossier*
  path; the understanding load/import capability is a separate concern owned by HLD 007.
- **The naming change is split, and BR-45's prose half is delivered.** HLD-007 LADR-05 records the
  boundary: product prose and the glossary ship now; the C# rename is a **separate change** bounded to
  type names. Discovery found that serialization is convention-based with no `JsonPropertyName`
  attributes, so a C# property name is the JSON field *and* the stored jsonb key — renaming DTO or
  document properties would break the API and the stored data, which BR-45's acceptance clause forbids.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-19 | Created — BRD-003 for understanding transfer and loading. Continues the shared requirement space at `BR-38` (after BR-37 durability); adds BO-11 … BO-15; defines Understanding as a first-class kind, the non-destructive load-by-default, the opt-in import via `--store`, import selectors and the Understanding naming. Amends BRD-002's import out-of-scope row for this capability. | BRD-003 |
