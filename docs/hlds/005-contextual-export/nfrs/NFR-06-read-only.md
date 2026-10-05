# NFR-06: Integrity — an export is a read

**Status:** Draft

## Requirement

An export performs **zero** writes. After any export, against any anchor set, the store is unchanged:

- Row counts identical across every entity, including the label registry — a facet observed during selection must not be registered as a side effect.
- Version chains identical: no new version, no change to which version is current.
- Graph identical: no vertex and no edge added, including the "missing" `contradicts` edge a contradiction finding describes (LADR-04).
- Blob storage identical: no object added. Bodies are read and hydrated, never rewritten.
- No file written anywhere except the dossier artefact, at the path the caller asked for, and the transient scratch intermediates composition needs (the bundle, LADR-02, and the skill's judgement inputs) under the gitignored scratch directory, removed when composition ends.

The guarantee is **structural, not behavioural**: the composing skill has no write capability at all
(LADR-08), so this cannot be violated by a change of mind in a composition pass — only by removing the
structure.

**Amendment (2026-10-05, issue 182).** This NFR originally allowed only the dossier file. Judgement
needs the bundle on disk — the agent reads it to write its judgements, and composition re-runs per
focus against one saved bundle — so the skill saves the bundle (written owner-only, refused at any
path git does not ignore) and the agent's judgement inputs to
`.context/mimisbrunnr-saga-dossier/scratch/`, which the workflow deletes when composition ends, also on
failure or cancel. LADR-02 already names the bundle an intermediate, never a deliverable. Store writes
remain zero; nothing above about rows, versions, the graph or blobs changes.

## Verification

- **L1** — snapshot every table's row count and the graph's vertex and edge counts; run an export over a slice containing contradictions, duplicates and unregistered facets; assert every count identical.
- **L1** — assert no blob object is created, using a counting blob-storage test double.
- **L1** — assert the selection path never calls `SaveChanges`, as a direct assertion on the context rather than an inference from counts.
- **Skill-level test** — assert the dossier skill exposes no write operation. A capability-absence test, so adding one breaks the build.
- **L1** — run an export over a slice whose facets are absent from the registry; assert the registry is unchanged. This is the most plausible accidental write, because registering an observed facet looks helpful.
- **L1** — the same zero-write snapshot over a slice whose selected items are `kind = understanding`. Assert no row, version, vertex, edge or blob change — the kind is not an exemption from read-only (LADR-15).

## Acceptance Criteria

- Every row, version, vertex, edge and blob count is identical before and after an export.
- That identity holds for an export selecting `kind = understanding` items; the zero-write guarantee is kind-independent (LADR-15).
- The selection path issues no `SaveChanges`.
- The composing skill has no write capability, proven by test rather than by policy.
- An unregistered facet encountered during selection remains unregistered.
- The only artefact produced is the dossier, at the requested path; any other file is a transient scratch intermediate under the gitignored scratch directory, removed when composition ends.

## Applies To

Goal 3 (findings as output, not writes) and the guiding principle. LADR-06 (findings are output),
LADR-08 (structural read-only). `BR-31`, and `BR-13` inherited from BRD-001.
