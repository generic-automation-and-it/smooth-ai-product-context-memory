# LADR-01: The snapshot is a third artefact — backup semantics, not a projection

**Status:** Draft

## Context

Two artefacts already leave the store: the forensic dump (whole-store readable projection,
serving BR-17) and the contextual export (curated, scope-filtered document, BRD-002). Both are
projections — lossy by intent, shaped for a reader. Neither can restore the store, and extending
either into a backup would break its contract: the dump is human-readable Markdown, and the
export deliberately excludes hidden material (BR-33), which a backup must contain.

## Decision

**Introduce** the corpus snapshot as a third artefact with backup semantics: bytes-faithful,
complete (hidden dimensions, superseded versions, proposed records included), machine-oriented,
restorable. It shares no code path with the forensic dump or the export — the same reasoning
HLD 005 LADR-01 applied to keeping the export off the forensic code path, in the opposite
direction: a projection path grown into a backup under-captures; a backup path grown into a
projection over-discloses.

Because it contains everything, the artefact is sensitive by default in the sense BRD-002's
BR-34 established for exports: destination visible, never committed or synchronised by default.

## Alternatives Considered

- **Extend the forensic dump to be restorable** — makes a human-readable projection load-bearing for durability; format changes for readability would silently break restore.
- **Rely on independent pg_dump + object-store copy** — status quo; nothing asserts the two captures describe the same moment, which is exactly HLD 001 NFR-03's unproven claim.

## Consequences

- Backup, readability and curation each keep a single-purpose artefact; none inherits the others' constraints.
- A third artefact is a third thing to document and maintain.
- The snapshot's completeness is what makes it sensitive; the two properties are inseparable and both stated.

## Related

- **HLD 005 LADR-01** — same separation argument, applied to export vs forensic dump.
