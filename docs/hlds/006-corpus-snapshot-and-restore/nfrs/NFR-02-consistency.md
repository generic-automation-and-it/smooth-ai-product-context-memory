# NFR-02: Consistency

**Status:** Draft

## Requirement

A restore into an empty target reconciles exactly against the manifest: row, version, vertex,
edge and object counts match; 100% of blob references in the restored database resolve to an
object whose content hash equals its address (zero dangling references); a bounded traversal
succeeds against the restored graph. This supersedes and closes HLD 001 NFR-03's Draft claim
("two stores restore to a mutually consistent state") for the snapshot path.

## Verification

L1/L2 round-trip test: seed a corpus with memories, versions (including superseded), edges,
ticket hierarchy and bodies; snapshot; restore into a fresh scratch database and empty object
store; assert the printed reconciliation closes on every count and the traversal returns the
seeded paths. Negative control: remove one blob entry from the archive and assert restore fails
its reconciliation rather than completing. Operationally, `scripts/verify-graph-restore.sh`
lineage is superseded by the restore command's built-in reconciliation.

## Acceptance Criteria

- Every count in the printed reconciliation matches the manifest exactly, on every restore.
- Zero dangling references in the restored database; every blob hash re-verified against its address.
- A restore missing any referenced entry fails loudly; no partial restore reports success.
- Restore refuses a non-empty target without an explicit override.
- Recall-feedback absence after restore is expected and asserted, not reported as loss.

## Evidence

- Reconciled counts are read back from the restored tables and AGE labels inside the restore
  transaction and checked before commit; a mismatch rolls back. L1
  `SnapshotRestoreRoundTripTests`: round trip into a fresh database and fresh bucket closes with
  `Committed = true`; a silently dropped edge (endpoint vertex absent) rolls back and leaves the
  target empty; a removed blob entry fails before any mutation; a non-empty target is refused.
- Not yet evidenced: recall-feedback absence assertion after restore; restore of an archive that
  recorded dangling references at capture (currently unrestorable — open finding).

## Applies To

Goals 1 and 3; the restore command (LADR-05) and corpus membership (LADR-03).
