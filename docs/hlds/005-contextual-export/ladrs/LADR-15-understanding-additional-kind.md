# LADR-15: `kind = understanding` is a first-class selectable kind on this read-only export

**Status:** Draft

## Context

HLD-005's original design (BRD-002, `BR-18`..`BR-36`) predates the Understanding kind. BRD-003
(`BR-38`..`BR-45`, HLD-007) later introduced an Understanding as a memory of `kind = understanding`,
stored in the existing models. HLD-007's vocabulary states only that an Understanding "rides on [the
forensic dump] as a kind" — it does **not** define the dossier/bundle side.

The only place HLD-005 asserts the Understanding as a selectable kind is a single line in its
`AGENTS.md` Key Behaviours (line 93) plus a changelog note, and a one-line mention in the scaffolded
worktask deliverable. It is **absent** from the HLD-005 `README` Key Goals and per-goal Definition of
Done, from every NFR-01..07 Verification and Acceptance list, from every LADR-01..14, and from BRD-002's
requirement space. So the selection and composition semantics of an Understanding on the bundle/dossier
are **unspecified** by the design as written.

This is the exact "specified-but-not-really" shape the design's own gap-detection discipline exists to
catch. Leaving it unspecified invites an implementer to invent semantics — or, worse, to assume the
original design covers them and ship a bundle that either excludes the kind or handles it differently
from every other kind, in both cases violating the same guarantees the design states for the others.

## Decision

An Understanding is a **kind like any other** on the bundle/preview and in the dossier. Nothing about its
selection, composition, citation, focus or reconciliation is a new surface; it reuses the same code
paths, the same bounded sets and the same guarantees. Concretely:

- **Select** `kind = understanding` by the same anchor set, combination rule, widening bound and
  scope gating as every other kind. `ExportRenderer` and `QueryMemories` already treat `kind`
  generically (`Understanding_kind_renders_with_all_five_parts` serves as evidence the kind is a stored
  value, not a special shape) — reuse that, add no new shape.
- **Cite and attribute** it to the NFR-05 bar and to HLD-007 NFR-03's attribution standard: memory
  identity, version, capture time preserved, and proposed-status positions flagged as proposed rather
  than shipped.
- **Reconcile** it in the same closed arithmetic (NFR-04); apply the same confidentiality (NFR-01),
  reproducibility (NFR-02), bounded-cost (NFR-03), read-only (NFR-06) and fidelity (NFR-07) guarantees.
- **Focus** applies to it like any other selected material; it is not exempt from `outside-focus`
  omission reporting.

This does **not** reopen an import or write path. The Understanding **load/import** capability and the
`--currentsession` dump remain owned by HLD-007 (BRD-003) and stay out of this read-only dossier's scope
(LADR-08). The only thing this LADR settles is that an Understanding *present in the store* is a
selectable, composable kind on this read-only export.

## Alternatives Considered

- **Treat the Understanding as out of scope for the dossier** — rejected: an Understanding is a memory
  of a given kind, and the exporter already composes memories by kind. Excluding it would make the
  dossier silently incomplete for any store that holds one, and would conflict with the AGENTS.md line
  that already asserts it rides on this export.
- **Give the Understanding a dedicated composition path** — rejected: the guarantees would need to be
  re-derived per kind, and a second path is exactly where a confidentiality or read-only guarantee
  silently drifts. One path, one set of guarantees.
- **Leave the semantics unspecified** — rejected: it is the status quo that produced this gap, and the
  design's own discipline is that a gap is closed explicitly or recorded as a deferral, not inherited.

## Consequences

- An Understanding in the store is selectable and composable on the bundle/dossier with identical
  semantics and guarantees to every other kind.
- The load/import capability stays where LADR-08 and HLD-007 put it; this adds no write path.
- NFR-01..07 must each name the Understanding-kind case in their Verification and Acceptance lists
  before they are declared satisfied. This LADR is not satisfied by authoring it — it is satisfied when
  the case is covered and the evidence exists.
- The design is now consistent with the AGENTS.md Key Behaviour that asserted the kind, closing the
  three-way drift between README, NFRs and AGENTS.md.

## Related

- **LADR-08** — this adds a kind, not a surface; the read-only structural guarantee applies unchanged.
- **HLD-007 NFR-03** — the attribution bar a dossier-carried Understanding must meet.
- **NFR-01..07** — each gains the Understanding-kind case in its Verification/Acceptance lists.
- **BRD-003 / HLD-007** — owner of the kind and of its load/import.
