# NFR-03: Auditability

**Status:** Accepted

## Requirement

- **Every mutation appears in a digest.** A write that changes the store without reporting it is a defect.
- The digest carries **counts for each outcome** — created, versioned, linked, diverged, skipped, labels proposed.
- **Skipped distinguishes its causes.** A candidate held back for bundling and a duplicate link skipped at write time are different events and are reported separately.
- **Dry-run renders an identical digest** to the write it predicts.

## Verification

- Execute a batch exercising every outcome; assert each appears with a correct count.
- Execute the same batch as a dry-run; assert the digest is identical in shape and counts, and that nothing persisted.
- Execute a batch producing both an atomicity split and a duplicate link; assert two distinct skipped figures rather than one aggregate.
- Assert a write path that mutates without contributing to the digest fails review — the digest is assembled from outcomes, not narrated separately.

The dry-run comparison is the load-bearing check: it proves the two paths share one implementation,
which is the only thing that makes dry-run predictive.

## Acceptance Criteria

- Every outcome type is represented with an accurate count.
- Dry-run and write digests match for the same input.
- Skipped causes are separately reported.
- No mutation path exists that bypasses digest assembly.

## Applies To

Goal 4; LADR-07, LADR-03, LADR-05.
