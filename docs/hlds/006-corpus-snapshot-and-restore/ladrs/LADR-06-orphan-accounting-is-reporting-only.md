# LADR-06: Orphan accounting is reporting only — it never deletes

**Status:** Draft

## Context

The membership walk (LADR-03) identifies unreferenced objects as a byproduct. The tempting next
step is to sweep them. HLD 001 records why that is dangerous: deleting an object can destroy
content another version still references, and its migration plan explicitly defers the
garbage-collection sweep as "deferred, not solved".

## Decision

**Report** orphan counts and identities; **never delete**. This design adds no deletion
capability of any kind to the object store. The accounting it produces — how many unreferenced
objects, growing at what rate — is precisely the measurement the deferred GC design needs to
justify itself, and producing the measurement must not quietly become performing the sweep.

Dangling references (the inverse defect) are likewise reported, never repaired: a repair is a
write with judgement attached, and it belongs to a deliberate operator action, not a backup path.

## Alternatives Considered

- **Sweep during snapshot** — turns a read path into a destructive write, contradicts HLD 001's stated deferral, and makes snapshot failure modes destructive.
- **Sweep behind a flag** — a flag away from data loss on the one path that exists to prevent it.

## Consequences

- The snapshot path is provably read-only against both stores (NFR-04), which keeps its failure modes harmless.
- Orphans continue to accumulate — accepted; that is the deferred design's problem, now with data.
- The GC design, when it comes, starts from measured counts instead of assumption.

## Related

- **LADR-03** — produces the accounting this decision constrains.
