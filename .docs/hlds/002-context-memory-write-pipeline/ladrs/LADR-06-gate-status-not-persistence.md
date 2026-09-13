# LADR-06: Approval gates status, not persistence

**Status:** Accepted

## Context

Some kinds of knowledge — rules, requirements, decisions — become **citable canon** once recorded, and
a wrong one propagates into future work as though settled. The risk posture says to ask about what is
irreversible.

The obvious reading is to withhold the write until a human approves. But capture happens at an
end-of-task checkpoint, which is exactly the moment a session is ending. Withholding the write means
the fact is lost whenever approval does not arrive before the session closes — which is most of the
time.

The reading is wrong because it misidentifies the irreversible step. **Being recorded is reversible.
Becoming citable canon is not.**

## Decision

**Gate the status, not the persistence.** A gated kind is written immediately with a proposed status
and promoted to approved by an ordinary version bump.

A proposed record is excluded from retrieval by default, or explicitly flagged where surfaced, so it
cannot be mistaken for settled fact. Promotion is the same mechanism as any other claim change, so
gating adds no new state machine.

Gating defaults **on** for the gated kinds, per the rule that irreversible steps are asked about.

## Alternatives Considered

- **Withhold the write until approval** — rejected: discards the fact if the session ends first, and the checkpoint is the moment sessions end.
- **Gating off by default** — rejected: makes canon the default outcome for exactly the kinds where a wrong entry is most expensive.
- **A separate pending store** — rejected: a second store with its own lifecycle, promotion path and failure modes, to express what a status field already expresses.

## Consequences

- No fact is lost to an absent approver.
- Proposed knowledge is visible to the write path — so a later capture on the same subject versions it rather than duplicating it.
- Retrieval must honour the proposed status consistently, or the gate is decorative.
- The store contains unapproved records by design, which any consumer must account for.

## Related

- **LADR-07** — the digest through which promotion is decided.
