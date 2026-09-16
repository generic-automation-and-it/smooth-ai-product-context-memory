# LADR-05: Restore ends with a printed reconciliation against the manifest

**Status:** Draft

## Context

The existing restore evidence (HLD 003 NFR-03) proved a round trip once, operationally, against
a scratch database. That demonstrates the mechanism works; it does not make any particular
restore trustworthy. Sufficiency — "the archive was enough to rebuild the corpus" — is a claim
only a restore can test, and it must be tested every time, not once at design acceptance.

## Decision

**End** every restore with a reconciliation the operator sees: counts in the manifest against
counts in the restored stores (rows, versions, vertices, edges, objects), every restored blob
reference resolved with its hash re-checked against its address, and a bounded traversal
executed against the restored graph. The reconciliation is printed, not merely asserted in a
test — the person holding the restored store must see the arithmetic close, the same reasoning
HLD 005 NFR-04 applies to dossier completeness.

Restore **refuses a non-empty target** unless explicitly overridden. Merging a snapshot into
live data has no defined semantics in this design; silent merge is how a restore destroys the
thing it exists to protect (BR-13).

## Alternatives Considered

- **Assert reconciliation in an L1/L2 test only** — proves the code path once; says nothing about the archive in hand or the target it landed in.
- **Restore-then-trust** — the status quo; a restore that "completed" with a missing blob is discovered at first read, months later.

## Consequences

- Every restore self-certifies or loudly fails; a partial restore cannot pass silently.
- Restore into an empty target is the only first-class path — deliberate, and cheap for a single-user local store.
- The printed reconciliation doubles as the acceptance evidence format for NFR-02, so operational runs and design verification read identically.
