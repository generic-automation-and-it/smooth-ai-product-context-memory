# LADR-08: Ticket hierarchy is practitioner-declared, not derived

**Status:** Accepted, owner-approved and implemented; release gates passed on 2026-09-15.
[Final verification](../../003-graph-edges-on-age/nfrs/NFR-02-ticket-traversal-measurements.md) closes
the ticket performance gate without relaxing its threshold.
**Owner:** generik0
**Decision date:** 2026-09-14

## Context

[HLD-003 LADR-08](../../003-graph-edges-on-age/ladrs/LADR-08-captured-ticket-hierarchy.md) resolves
ticket representation with exact provider/key identities and current-state parent -> child
`TICKET_PARENT` declarations. A vertex backfill alone creates no hierarchy. The writer decision
belongs here, not to the read-only export design. Existing LADR-05 derives memory links from subject
matching; that is not evidence of ticket parentage.

## Decision

- Capture only a **practitioner-declared** ticket parent/child relationship. The skill may carry an
  explicit declaration made during work into the normal capture checkpoint, but must not infer one
  from ticket names, group association, memory content or memory links. No tracker crawl, scheduled
  reconciliation, read-time upstream projection or automatic hierarchy derivation.
- Use explicit set/reparent/remove with the expected parent (including explicit absence for set),
  exact provider/key identities, mandatory reason/source for the new declaration, optional
  `observedAt` and server-owned `recordedAt`. The API owns transactional mechanics, one-parent and
  cycle checks, and stale-expectation rejection under HLD-003 LADR-08's shared advisory lock.
- The API validates `ExpectedParent` before checking identical current state. Identical
  parent/reason/source/observedAt with the expected **current** parent yields `changed: false` and
  preserves `recordedAt`. Replaying the initial null expectation after a successful set conflicts;
  this is state comparison, not operation replay. There is no replay token.
- Hierarchy is current state, not append-only history. Reparent replaces the declaration; remove
  deletes it. Do not route it through proposed memory status, silently replace a parent, or create
  memory `LINKS` as a substitute. LADR-05's memory-link derivation and LADR-06's memory status gate
  remain unchanged and do not authorize inferred hierarchy writes.
- Group ticket association stays in JSONB. Group-ticket triggers and backfill maintain identities,
  not parent edges; sharing a group never declares parentage. Existing exact ownership and additive
  group-ticket behavior are preserved. No Ticket-to-Memory membership edges are written.
- The capture receipt names the declared operation and whether it succeeded or conflicted; a stale
  expectation is not silently counted as a successful write. Pre-write inspection must show the
  same intended operation without writing. Read-only findings never invoke this writer.
- Implemented transport is `ticket-parent` -> PUT `/api/context/tickets/parent` -> `SetTicketParent`
  -> `ITicketGraph.ChangeParentAsync`. The API returns `changed`; the skill reports the operation and
  complete receipt. `parent` is required on the wire (null removes); null/absent `expectedParent`
  expects absence, while the skill requires it explicitly. Reason/source are mandatory for all
  operations, including remove. Local `--dryrun` validates shape without network or writes, not
  current ownership/cycles/preconditions. A hierarchy request is separate from the memory-set transaction.
- Always distinguish captured declarations from tracker truth: undeclared upstream hierarchy was
  not followed and freshness is unverified. Retrieval applies live ownership and explicit scope
  consent under HLD-003 LADR-08, not consent inferred from knowing a ticket.

## Alternatives Considered

- Extending subject-based memory-link derivation to ticket hierarchy is rejected: related claims do
  not establish ticket parentage, and fanout would manufacture provenance.
- Tracker reconciliation or live projection is rejected: it introduces upkeep or network dependency
  and implies freshness this store cannot establish.
- Requiring a full dossier implementation first is rejected: declaring a ticket relationship is a
  capture concern. The read-only dossier remains a separate discovery design.

## Consequences

- The ticket half of [HLD-005 LADR-11](../../005-contextual-export/ladrs/LADR-11-no-writer-derives-anchor-edges.md)
  is decision-resolved. Tag identity, synonyms and their writer remain blocked; no tag inference or
  taxonomy registry is approved here.
- Coverage is intentionally partial and possibly stale. It is disclosed, not filled by inference.
- API/store mutation, skill transport/local inspection and tests now exist. Verification covers
  explicit declarations, identity-only backfill, strict expected-parent conflicts, no-op state,
  receipts, replacement/removal and read-only findings. Final full-suite, explicit benchmark and
  deterministic skill checks passed; full dossier work remains separate from this accepted increment.

## Related

- [LADR-05](./LADR-05-link-derivation-batched.md): memory-to-memory derivation remains unchanged.
- [LADR-07](./LADR-07-digest-is-a-receipt.md): receipts report mutations; inspection precedes them.
- [HLD-003 LADR-08](../../003-graph-edges-on-age/ladrs/LADR-08-captured-ticket-hierarchy.md): authoritative identity, lifecycle, locking and traversal contract.
