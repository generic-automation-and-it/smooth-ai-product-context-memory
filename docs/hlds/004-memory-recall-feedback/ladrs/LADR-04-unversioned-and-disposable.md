# LADR-04: Feedback is unversioned and disposable

**Status:** Draft

## Context

The store treats knowledge as append-only: history is protected by a trigger, claims version rather
than update, and nothing canonical is destroyed.

Feedback is not knowledge. It records that a retrieval happened — a statement about findability, not a
claim about the world. Applying the same protections would be a category error, and an expensive one:
version chains of recall counts would fill history with noise while protecting something whose loss
costs nothing.

The design already draws this line. Tags and facets are unversioned for exactly this reason —
classification rather than claim — which is why they sit on the stable row rather than the versioned
one.

## Decision

**Treat feedback as unversioned, disposable data.** It does not version, it is not covered by the
append-only guarantee, and it may be discarded, truncated or reset without loss of knowledge.

Consequences follow directly. Feedback need not survive a restore, which removes it from the
cross-store consistency problem entirely. Retention can be a simple bound rather than a policy. And
resetting it before a tuning experiment is legitimate rather than destructive.

## Alternatives Considered

- **Protect feedback like knowledge** — rejected: a category error that would fill history with noise and extend a guarantee to data whose loss costs nothing.
- **Keep feedback indefinitely** — rejected: unbounded growth for a signal whose value decays. Old recall data describes a store and a recall implementation that no longer exist.

## Consequences

- No interaction with the append-only trigger, the version chain, or the model-shape guard.
- Excluded from backup and restore, so cross-store recoverability is unaffected.
- A tuning experiment can start from a clean baseline.
- Long-term trends are unavailable if retention is short — accepted, since the questions asked are about the present implementation.

## Related

- **LADR-02** — placement, which this materially simplifies.
