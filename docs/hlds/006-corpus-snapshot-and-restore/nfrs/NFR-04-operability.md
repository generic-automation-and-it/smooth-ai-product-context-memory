# NFR-04: Operability

**Status:** Draft

## Requirement

Snapshot, verify, restore and preflight are each one command, available from the published
Docker image without a .NET SDK (LADR-07): preflight and snapshot over HTTP on the running Host,
verify and restore as one-shot containers. No new container image is added; the stack stays API + database + object store. Snapshot and preflight are provably read-only against
both stores — zero writes to any live row, vertex, edge or object. Verify touches nothing but
the archive. Snapshot of the reference corpus size (thousands of memories, per BRD-001's volume
assumption) completes in single-digit minutes on the development machine, measured not assumed.

## Verification

Row-count/version-chain byte-equality assertion before and after snapshot and preflight (the
same mechanism HLD 005 NFR-06 uses for export read-only-ness). Container-count check against the
AppHost. Timed snapshot against a seeded reference corpus, with the measurement recorded in this
folder as evidence, following the HLD 003 NFR pattern.

## Acceptance Criteria

- Each operation is one command with no manual preparatory steps, runnable from the Docker release without an SDK.
- Both stores are byte-identical after snapshot and after preflight.
- Verify runs with no service and no store present.
- Container count unchanged.
- The timed measurement is committed as evidence before this NFR moves past Draft.

## Applies To

All goals; all four command surfaces; the AppHost topology.
