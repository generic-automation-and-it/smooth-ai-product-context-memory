# LADR-07: The digest is a receipt; dry-run is the veto

**Status:** Accepted

## Context

Every write returns a digest listing what changed. It is tempting to treat it as an approval gate —
show it, then commit on confirmation.

It cannot be one. The write is a single transaction that creates memories, bumps versions and writes
links together. By the time a digest can describe what happened, it has already happened. A digest
presented as a veto point would be a lie about when the transaction closed.

Yet pre-write inspection is genuinely wanted, particularly for a large or unfamiliar batch.

## Decision

**Classify** the digest as a **post-write receipt**, and provide a **dry-run** as the pre-write
inspection point.

The digest reports created, versioned, linked, diverged, skipped and labels-proposed, each with a
count, and is rendered after the transaction commits. Its purpose is auditing and deciding which
proposed records to promote — not veto.

Dry-run executes the **identical pipeline** and renders the **identical digest** while persisting
nothing. It must share one code path with the real write: every verdict — subject collision, missing
version target, unknown link endpoint, already-present link — is reached before the persist step
branches. A dry-run on a separate path stops predicting the real one, which is the only thing it is for.

The skipped count **distinguishes its causes**. A candidate held back for bundling never reaches the
API, while a duplicate link is skipped at the API — collapsing them into one number hides a split
remainder behind an unrelated zero.

## Alternatives Considered

- **Digest as an approval gate** — rejected: the transaction has already committed, so the gate would be fictional.
- **Two-phase write, prepare then commit** — rejected: a distributed-transaction shape for a single-user local tool, and a prepared transaction left open by an abandoned session holds locks indefinitely.
- **Dry-run as a separate estimation path** — rejected: it would drift from the real path and stop predicting it, which removes its entire value.
- **One aggregate skipped count** — rejected: hides an atomicity split behind a link-duplicate zero.

## Consequences

- Auditability without pretending to a veto that the transaction shape cannot support.
- Dry-run genuinely predicts the write, because it *is* the write minus persistence.
- Sharing one code path constrains implementation: the persist branch must sit at the end, after every verdict.
- A human wanting to prevent a specific write must dry-run first; there is no undo at digest time.

## Related

- **LADR-06** — the digest is where promotion is decided.
- **NFR-03** — auditability.
