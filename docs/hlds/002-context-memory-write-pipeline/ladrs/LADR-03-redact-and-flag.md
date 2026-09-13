# LADR-03: Redact and flag, never reject

**Status:** Accepted

## Context

When detection fires, the write can be rejected or the span scrubbed and the write allowed to proceed.

Rejection is superficially safer. But the fact and the secret are unrelated: a durable observation
about a system is not made less true by having a token pasted beside it. Rejecting discards knowledge
to solve a scrubbing problem, and the loss is silent — the session ends and the fact is gone.

## Decision

**Scrub** the sensitive span, **record** that redaction fired, and **proceed** with the write.

The digest names the candidate on which redaction fired and the rule that matched. It records
**neither the matched span nor the content** — the confidentiality constraint forbids logging content,
and a log line naming the secret would defeat the stage that just removed it.

The record is **digest-only**. Persisting a redaction audit trail on the version was considered and
deferred: it would mean logging content-adjacent detail and extending the append-only trigger's
equality list. Revisitable if an audit trail is genuinely owed.

## Alternatives Considered

- **Reject on detection** — rejected: discards knowledge for an incidental problem, and the loss is silent.
- **Persist a redaction record on the version** — deferred: gives an audit trail at the cost of storing content-adjacent detail and extending a trigger's column list.
- **Log the matched span for diagnostics** — rejected outright: reproduces the leak in the log, where it is often less protected than the store.

## Consequences

- Knowledge survives an incidental secret; capture stays additive.
- The human learns redaction fired and can re-capture cleanly if the scrub damaged meaning.
- No audit trail of what was scrubbed exists beyond the digest, which is a deliberate deferral.
- A false positive silently degrades a fact. Mitigated by naming the matching rule in the digest, so the cause is visible.

## Related

- **LADR-02** — establishes the stage this governs.
- **LADR-07** — the digest that carries the record.
