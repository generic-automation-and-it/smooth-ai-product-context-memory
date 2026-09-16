# NFR-03: Confidentiality

**Status:** Draft

## Requirement

No snapshot, verify, restore or preflight output — logs, reports, reconciliations, error paths
included — contains memory content: no subject, claim, summary, body text or edge reason.
Identifiers, hashes and counts only (the store-wide NFR-05 rule of HLD 001, extended to this
surface). The artefact declares itself sensitive, its destination is visible at creation, and
the default destination is neither committed to version control nor synchronised.

## Verification

Log-capture assertions on every command's happy and error paths, seeded with sentinel content
strings, asserting the sentinels never appear in any output stream. Repository test: assert the
default artefact destination is matched by `.gitignore`. Manual review of the reconciliation and
orphan reports against a seeded corpus.

## Acceptance Criteria

- Sentinel content never appears in any output of any command, including failures.
- Orphan and dangling reports name addresses and identifiers, never content.
- The artefact's metadata declares sensitivity; its write destination is printed at creation.
- The default destination is gitignored and outside any synchronised path.

## Applies To

All goals; every command surface this design introduces.
