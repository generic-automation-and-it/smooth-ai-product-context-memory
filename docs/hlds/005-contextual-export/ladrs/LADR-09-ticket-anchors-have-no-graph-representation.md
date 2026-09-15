# LADR-09: Ticket anchors as graph vertices

**Status:** Accepted for captured ticket hierarchy; decision resolved by owner approval on 2026-09-14.
Implementation and release gates accepted on 2026-09-15 against
[final evidence](../../003-graph-edges-on-age/nfrs/NFR-02-ticket-traversal-measurements.md).

> **Resolution authority:** [HLD-003 LADR-08](../../003-graph-edges-on-age/ladrs/LADR-08-captured-ticket-hierarchy.md)
> explicitly supersedes HLD-003 LADR-02 in writing before any ticket migration. The previous
> memory-only representation block is resolved for tickets only, not arbitrary anchor graphs.

## Context

`BR-18` selects by ticket, and that works today: a ticket resolves relationally to a memory group, and
the group's memories become the anchor set (LADR-03). What does **not** work is any relationship *between
tickets*.

A ticket's blockers, the tickets an initiative contains, the ticket a ticket was split from, the ticket
that supersedes another — these are real relationships in the practitioner's work, tracked in whatever
issue tracker issued them, and completely absent from the store's graph. A slice anchored on a ticket
therefore cannot widen to "and everything about the tickets this one depends on", even though that is
frequently the shape of the question.

The original block was not an oversight. HLD-003 LADR-02 rejected descriptive properties on vertices because
every polyglot failure it surveyed failed the same way: one fact in two stores, drifting, with no
arbiter. A ticket vertex is that argument's hardest case — ticket identity is owned by an external
tracker, mirrored into a group's documents, and would then exist a third time in the graph.

## Decision

**Adopt the ticket-only contract in HLD-003 LADR-08.** `Ticket` carries exact `provider`/`key` only;
`TICKET_PARENT` is a practitioner-declared parent -> child edge with reason/source, optional
`observedAt` and mandatory `recordedAt`. One parent, no cycles, explicit expected-parent
set/reparent/remove, current state rather than history. The writer is decided in
[HLD-002 LADR-08](../../002-context-memory-write-pipeline/ladrs/LADR-08-practitioner-declared-ticket-hierarchy.md).

Membership stays group JSONB. Triggers maintain identities on group-ticket changes/backfill and
group deletion; group-ticket and hierarchy mutations share one transaction-scoped advisory lock.
Memories join relationally through live ownership, never through membership fanout or projected
memory `LINKS`. Existing exact provider/key ownership is preserved.

Ticket traversal is separate from memory provenance traversal: required `maxDepth` 1..5,
deterministic capped paths and distinct current non-proposed memories, including the anchor's.
Every ticket must resolve to exactly one live owner. `HiddenDimensions` gates every hop, dropping
the whole path rather than shortening it; endpoint `Plan()` narrowing applies to returned memories.
A ticket does not grant hidden-scope consent. Neither historical dossier selection nor general
blocker/dependency traversal is implied by this parent/child contract.

`ITicketGraph` now backs PUT `/api/context/tickets/parent` and POST `/api/context/tickets/paths`.
The read combines a Cypher anchor with recursive SQL over indexed AGE adjacency and live memberships,
not variable-length Cypher. Memory association uses selected capped path endpoint groups plus anchor,
not every admitted ticket. Always report **undeclared upstream hierarchy was not followed; freshness
is unverified**, plus visible-only cap flags without hidden IDs/counts. This is captured hierarchy,
not a synchronized tracker or a completeness claim. Release acceptance rests on final tests and
benchmarks, not API presence alone.

## Alternatives Considered

- Relational hierarchy would give up graph traversal; live tracker projection introduces a network
  dependency and unowned freshness obligations.
- Ticket-to-Memory edges are rejected: JSONB already owns association, and relational joins avoid
  membership fanout. Projecting hierarchy onto memory `LINKS` invents provenance and stays forbidden.
- General ticket relationships such as blockers, splits and supersession remain outside this
  decision. Their motivating examples above are not approval of additional edge types.

## Consequences

- Ticket representation and traversal are implemented; full dossier composition remains unimplemented.
- Captured hierarchy remains partial and potentially stale. Group deletion removes its vertices and
  incident hierarchy transactionally; memory deletion does not. Ticket-only Down warns of lost
  declarations while preserving memory `LINKS` and relational metadata (HLD-003 LADR-08).
- Existing HLD-003 NFR-02 memory budgets remain; composed ticket p95 <= 100 ms still gates release.
  The hub-active benchmark exercises the actual command; its gate passed without widening the budget.
- Tag identity and synonyms remain blocked under LADR-10.

## Related

- **LADR-03**: relational selection and memory widening remain separate from ticket traversal.
- **LADR-10**: tag identity and synonyms remain blocked.
- **LADR-11**: ticket writer resolved by HLD-002 LADR-08; tag writer still blocked.
- **HLD-003 LADR-08**: current authority; supersedes the historical LADR-02 restriction.
