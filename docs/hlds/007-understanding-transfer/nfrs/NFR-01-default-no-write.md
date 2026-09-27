# NFR-01: Integrity of load — zero writes on a default load

**Status:** Accepted

## Requirement

A default load (without `--store`) performs **zero** writes. After loading an Understanding export, a
session, meeting notes or any material, the store is unchanged:

- Row counts identical across every entity.
- Version chains identical — no new version, no change to which version is current.
- Graph identical — no vertex and no edge added.
- Blob storage identical — no object added.
- The label registry is unchanged — a facet or kind encountered while loading must not be registered.

The guarantee is tied to the default: the load path has no write side effect. Writing requires the
`--store` switch (LADR-02), and must be absent from the default path.

## Verification

- **L1** — snapshot every table's row count and the graph's vertex/edge counts; run a load over a slice
  containing Understandings, foreign material and unregistered facets; assert every count identical.
- **L1** — assert no blob object is created, using a counting blob-storage test double.
- **L1** — assert the default load path never calls `SaveChanges`, as a direct assertion.
- **Skill-level test** — assert the load skill's default invocation exposes/executes no write operation.

## Acceptance Criteria

- Every row, version, vertex, edge and blob count is identical before and after a default load.
- The default load path issues no `SaveChanges`.
- An unregistered facet or kind encountered during a default load remains unregistered.
- No file is written anywhere by a default load other than any agent-context the running session uses.

## Applies To

Goal 2 (loading injects into context, writes nothing), LADR-02. `BR-41`, and `BRD-002`'s `BR-31`
inheritance.

## Evidence (2026-09-26)

L1 `tests/SmoothAiProductContextMemory.Application.ComponentTest/Features/UnderstandingTransferStoreTests.cs` `Default_load_read_paths_write_nothing_to_any_store`: over a seeded slice (versioned understanding, repo-anchored decision, an edge, blobs, a registered label) it runs query at both breadths plus an unregistered facet and kind, the forensic export, and dossier bundle and preview, then asserts identical counts for all six entities, the identical current-version set, identical AGE vertex/edge counts, zero `SaveChanges` (interceptor count), zero blob stores (counting double), and the unregistered facet/kind still unregistered. The load client is file-only, so this is proven over the read paths that produce what a load consumes. **Scope boundary:** query records recall telemetry in `recall_feedback` (HLD-004 LADR-02) — a SQL-only table outside the six entities and excluded from backup/restore; it is not store content and is out of this NFR's scope. Skill L0 `.agents/skills/mimisbrunnr-understanding/tests/run_tests.py` covers the no-write default invocation.
