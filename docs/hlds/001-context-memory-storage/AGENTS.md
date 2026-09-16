# AGENTS.md - Context memory storage

AI Context: HLD for context memory storage. Updated: 2026-09-13

## TL;DR

How context memory is stored: PostgreSQL as an index over content, bodies in content-addressed object
storage. Intent and goals in [README.md](./README.md); decisions in [./ladrs/](./ladrs/); measurable
quality bar in [./nfrs/](./nfrs/); containers and entity model in
[./diagrams/c4-context.md](./diagrams/c4-context.md). The business requirements this design answers
— and the only place their justification belongs — are in
[BRD 001](../../brd/001-context-memory/): principally BR-05 (cross-product recall), BR-08 (superseded
knowledge stays readable), BR-09 (a correction to a record is distinguishable from a change in the
world), BR-13 (nothing silently lost) and BR-17 (readable without this application).

## Non-Negotiables

- **A change that weakens a cited `BR-NN` is a business decision, not a design one.** Storage decisions may trade against each other freely; trading away BR-09's two axes or BR-13's retention is out of this document's authority and belongs back at the BRD.
- **Do not flatten the stable memory row into the versioned one.** It reads as unnecessary indirection until you try to attach an unversioned tag (LADR-03).
- **Do not move tags or facets onto the versioned row.** Their placement *is* the unversioned-classification decision.
- **Do not add a foreign key from facets to the label registry.** It is advisory by design; an FK makes it enforcing (LADR-07).
- **Do not reintroduce tables for tags, facets, sources, repositories or tickets.** Six entities were removed deliberately; the HLD 003 cutover then dropped `MemoryLink`, so the guard test enforces exactly **six** (LADR-02, amended). Relationships live in AGE, not a seventh table.
- **Do not move history into a JSONB array.** Appending to a JSONB array is read-modify-write, so concurrent appends silently lose one (LADR-02).
- **`created_on` is not redundant beside `valid_from`.** Removing it makes correcting a record indistinguishable from the world changing (LADR-05).
- **`kind` must not become an enum or a check constraint.** Open vocabulary is deliberate.
- **Never plain `SET` for the history-delete bypass** — `SET LOCAL` inside a transaction only. A session-scoped setting leaks across pooled connections (LADR-07).
- **Do not weaken a failing tier-2 assertion to make an unrelated change pass.** The failure is the mechanism working.

## Architecture Decisions

See [./ladrs/](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| [LADR-01](./ladrs/LADR-01-postgresql-single-engine.md) | PostgreSQL for relational and document data | One transaction spans all of it; a second store could not join it |
| [LADR-02](./ladrs/LADR-02-hybrid-placement-rule.md) | Typed columns for hot filters, JSONB for the long tail | Decides where any new field goes. Every JSONB document carries a shape marker that cannot be retrofitted |
| [LADR-03](./ladrs/LADR-03-stable-entity-versioned-child.md) | Stable entity, versioned child | Subject is stable, claim is volatile. Code reading a subject from the version row is wrong |
| [LADR-04](./ladrs/LADR-04-versioning-absorbs-supersession.md) | Versioning absorbs supersession | Bump ordering and its single transaction are not optional — see Key Behaviors |
| [LADR-05](./ladrs/LADR-05-bitemporal-separation.md) | Two independent time axes | Neither may be derived from the other |
| [LADR-06](./ladrs/LADR-06-content-addressed-blob-storage.md) | Content-addressed bodies, database holds the reference | Objects are immutable; a bad write can only be orphaned |
| [LADR-07](./ladrs/LADR-07-enforcement-tiers.md) | Constraint / mechanism / verified-soft | Tells you whether a guarantee is structural or behavioural before you rely on it |

## Key Behaviors

- **Version bump ordering is fixed and transactional.** Flip the old current off *before* inserting the new one, both in one transaction. The partial unique index prevents two currents but nothing prevents *zero*, so a failure between two round-trips strands a memory with no current version — a state no constraint forbids and nothing detects.
- **Retrieval defaults to current-only; the store keeps everything.** Current-only is a query default, never a storage rule.
- **Subject uniqueness is exact-match only.** The index catches identical strings; semantic equivalence is the write path's responsibility and cannot be a constraint.
- **Ticket uniqueness has no database enforcement** since tickets are denormalised. It is checked in the write path's existing read-before-write pass.
- **Cascade deletion of history requires the transaction-scoped bypass.** Without it the trigger raises; with plain `SET` it leaks to the next caller on that pooled connection.
- **Label usage is a derived view, not a column.** Never write a usage count.
- **Adding a column to a history table requires extending the append-only trigger's equality list** in the same migration, or the new column becomes silently mutable through an otherwise legal version-bump update.

## Quality Constraints

Targets and verification live in [./nfrs/](./nfrs/). Two shape how code is written:

- **Every predicate must be executed by the database.** Filtering materialised rows in a handler defeats the indexes and pulls whole version chains across the wire on a current-only query — a performance failure regardless of wall-clock time.
- **No logging call may take memory content as an argument**, on any path including errors. Identifiers and counts only.

## Migration Plans

- Unreferenced objects accumulate in the object store; a garbage-collection sweep is deferred, not solved. Orphaning drops the database reference only — deleting the object can destroy content another version still references. The application abstraction (`IBlobStorage`) therefore carries **no delete capability** (guarded by `BlobStorageCapabilityGuardTests`); the concrete adapter's delete exists for isolated test cleanup only. HLD-006 reports orphan/dangling counts but never deletes or repairs. **Reopening condition for a GC design:** HLD-006 orphan accounting demonstrates material growth, and the design proves an address has zero references across *all* versions before deletion.
- Candidate recall is stemmed: the text-search configuration is `english` on both the GIN index expressions and the query side, selected on measured evidence ([NFR-02 recall-tuning measurements](./nfrs/NFR-02-recall-tuning-measurements.md) — recall 0.44 → 0.78 at 0.88 precision). Trigram similarity (`pg_trgm`) was measured and **rejected** (no recall gain over stemming; extension + threshold-sensitive semantics); its reopening threshold is recorded in the same evidence document. Index and query configuration must change together or the index silently stops serving.
- The generated Markdown projection (`dotnet run --project src/SmoothAiProductContextMemory.Host -- export`) pays down the inspectability debt content addressing introduces. NFR-04 is Accepted: L1 `ExportStoreHandlerTests` pins byte-identical re-runs, inlined bodies, and missing-blob warn-and-continue.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-16 | Recall deferral closed by measurement: FTS configuration `simple` → `english` (recall 0.44 → 0.78, precision 0.88); `pg_trgm` measured and rejected with a recorded reopening threshold. | [NFR-02 recall-tuning measurements](./nfrs/NFR-02-recall-tuning-measurements.md) |
| 2026-09-16 | Orphan-management ambiguity closed structurally: `IBlobStorage` no longer exposes deletion; test-only delete stays on the concrete adapter; GC remains deferred behind the recorded reopening condition. HLD-006 reports orphans, never deletes. | `BlobStorageCapabilityGuardTests` |
| 2026-09-13 | NFR-04 Accepted — export projection + byte-identical L1 assertions shipped. | NFR-04 |
| 2026-09-13 | Guard is six entities: HLD 003 dropped `MemoryLink`. Relationships live in AGE, not a seventh table. | HLD-003 |
| 2026-09-13 | Created — converted from ADR-0001 and ADR-0002, which were one design in two documents. | ADR-0001, ADR-0002 |
| 2026-09-13 | Added the upstream BRD as cited business authority and the rule that weakening a cited `BR-NN` escalates. | BRD 001 |
