# LADR-02: Version snapshots and reconcile restored journals explicitly

**Status:** Core restore detection verified; explicit reconciliation implementation under acceptance.

## Context

BR-51 requires records and operation identities to restore consistently. Restoring an older corpus beside a newer journal makes a journal-only success claim false and makes blind replay overwrite an intentional recovery point.

## Decision

Writers emit snapshot format v4 with a required hashed operation-receipt member and count. Readers accept v3 without receipts. They refuse unsupported versions, missing/inconsistent v4 receipt declarations, tampered content, and v3 manifests that smuggle a newer receipt contract. Snapshot verify remains available offline. Disposable recall feedback is excluded and cleared on restore. The runtime corpus state is regenerated, never restored with the old epoch.

Restore rotates the epoch. Before replay, the service compares its journal epoch with current core state and confirms committed operations against the core receipt store. A mismatch becomes an explicit reconciliation state; it does not automatically resend an old plan. The caller is told the observed epoch and absent-operation condition.

`POST /api/knowledge/captures/{id}/reconcile` is write-authorized and explicit. Its idempotency key, acknowledged restore and expected current epoch bind one recovery decision. The journal retains an audit of old epoch, plan, pending/committed operation identities and generation. Reconciliation preserves settled usage and uncertain reservations, then clears obsolete comparison state and re-reads/re-plans against the restored corpus under a new operation generation. Repeating the same request returns that generation; changing its payload under the same key conflicts. A racing second restore is rejected by epoch validation. Purged source material cannot be reconstructed from a receipt and is explicitly refused rather than invented.

A corpus snapshot is not a service journal backup. Operators preserve both according to OPERATIONS.md. Restore runs as a one-shot core process against the isolated or deliberately stopped installation, not beneath a serving API. Disruptive acceptance uses only synthetic stores.

## Consequences and alternatives

Automatic reapplication was rejected because recovery intentionally changes history. Rejection without a usable reconciliation operation was also rejected: pause alone leaves no implemented route to recover acknowledged work. Explicit recovery preserves control while fresh comparison and CAS protect current knowledge. Remaining budget may require an explicit bounded allowance extension; restart or reconciliation does not reset it.

## Verification

Archive tests cover v3/v4 and refusal cases. Real SIGKILL tests cover acknowledgements, reservations and the core-commit/journal-save gap. Actual older-snapshot restore proves epoch rotation and no automatic replay. Extended acceptance must complete explicit reconciliation, replay it idempotently, verify historical audit/usage preservation and confirm newly incorporated knowledge through direct reads.