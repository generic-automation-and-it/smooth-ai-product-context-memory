# NFR-01: Integrity

**Status:** Draft

## Requirement

Verification of an archive detects any single-byte alteration, truncation, or missing entry,
offline, using only the archive — zero false passes. For every body entry, the recomputed hash
must additionally equal the database-cited address captured in the same archive, and a mismatch
is classified as capture-time inconsistency, reported distinctly from archive corruption.

## Verification

Tamper-suite test: take a snapshot of a seeded corpus, then for each mutation class — flip one
byte in a blob entry, flip one byte in the database dump, truncate the archive, remove one
entry, alter the manifest itself — assert verification fails and names the affected entry.
Positive control: the unmodified archive verifies clean. Classification test: seed a corpus with
a deliberately mismatched blob (content ≠ address), snapshot, and assert the report says
capture-time inconsistency, not corruption.

## Acceptance Criteria

- All tamper-suite mutation classes are detected and the damaged entry is named.
- An unmodified archive verifies with zero findings.
- The capture-time-inconsistency classification is distinguishable in output from transit corruption.
- Verification requires no running service, no database and no network.

## Applies To

Goals 2 and 3; the manifest (LADR-02) and the verify command.
