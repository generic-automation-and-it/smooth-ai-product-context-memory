# LADR-11: Capture-time derivation of anchor edges

**Status:** Ticket design Accepted, owner-approved and decision-resolved on 2026-09-14; implemented
and release gates passed on 2026-09-15. Tag half remains **Blocked**.

> **Ticket resolution:** [HLD-002 LADR-08](../../002-context-memory-write-pipeline/ladrs/LADR-08-practitioner-declared-ticket-hierarchy.md)
> selects practitioner-declared hierarchy only, using the representation accepted by
> [HLD-003 LADR-08](../../003-graph-edges-on-age/ladrs/LADR-08-captured-ticket-hierarchy.md).
> **Remaining tag blocker:** LADR-10's tag identity/synonym decision must clear, then HLD-002 must
> decide its writer. Ticket approval does not provide a tag writer or authorize derivation.

## Context

LADR-09 and LADR-10 originally blocked on *representation*. LADR-09 is now resolved for declared
ticket hierarchy; LADR-10 remains blocked for tags. This LADR is the writer-side half of that gap:
adding an identity label alone would still produce no hierarchy edges.

The existing derivation is memory-to-memory and subject-driven. It locates existing subjects, decides
version-bump versus new-memory versus skip, and derives links from that comparison. Every relation it
can produce connects two memories. `BR-11` also requires derived relationships to be *proposed* to the
practitioner rather than written silently, so a new edge kind needs a proposal shape and a review
presentation, not just a writer.

There is a second question hiding behind the first. Ticket and tag relationships mostly already exist
somewhere authoritative — a tracker knows a ticket's blockers, and a vocabulary owner would know a tag's
synonyms. Deriving them at capture time would create a second copy of a fact owned elsewhere, which is
the exact drift argument HLD-003 LADR-02 used to keep vertices thin. So the writer question is not only
*how* but *whether* — and possibly the answer is that these edges should be projected at read time from
an authoritative source rather than captured at all.

## Decision

**Tickets: practitioner-declared only**, as accepted by HLD-002 LADR-08. The skill carries an explicit
declaration into capture; it does not derive parentage from shared group membership, subject matching,
ticket spelling or tracker reads. Set/reparent/remove requires expected parent, exact provider/key,
and the declaration metadata defined by HLD-003 LADR-08. Receipt and pre-write inspection distinguish
the declared operation from derived memory links. No proposed-memory surrogate or memory `LINKS`
projection is permitted.

The API owns mechanical one-parent/cycle/precondition validation and atomic current-state mutation.
Group-ticket triggers/backfill create identities only, never parent declarations. JSONB membership
remains authoritative; a relational join associates memories without fanout. Captured coverage may
be thin or stale and is disclosed as such, not expanded by inference.

**Tags: no decision taken.** No tag edges are derived, proposed or written. Clearing ticket
representation does not clear tag identity, synonym semantics or the tag writer decision.

**Implementation:** `ticket-parent` sends PUT `/api/context/tickets/parent` through `ITicketGraph`.
Strict expected-parent comparison occurs before identical-state no-op detection; replaying an old
null expectation after set conflicts, and no replay token exists. Local dry-run validates only shape,
not ownership/cycles/preconditions. Read-only findings may describe missing relationships only with an evidence basis and
scope qualification; they never invoke the writer. The evidence-only `near-miss-tag` interim is
defined in LADR-10, not permission to build the full dossier or broaden selection.

## Alternatives Considered

- Automatic ticket-edge derivation is rejected: subject similarity and association do not prove
  parentage. Tracker reconciliation adds upkeep; live projection adds a network dependency.
- Practitioner declaration is selected for ticket hierarchy only. No alternative for tag edges
  can be selected until LADR-10 and the HLD-002 writer decision resolve.

## Consequences

- Ticket representation and writer are implemented and accepted against the final HLD-003 evidence.
- Tag-level widening remains blocked. Evidence-only findings can inform a future vocabulary decision
  but neither establish synonyms nor authorize additional searches or writes.
- The evidence-only near-miss helper is executable; full dossier implementation remains outside
  this ticket-only approval. HLD-003's performance gate passed, with no relaxed threshold.

## Related

- **LADR-09**: ticket representation resolved; **LADR-10**: tag representation remains blocked.
- **LADR-06**: a noticed missing relationship becomes a finding, not a write.
- **HLD-002 LADR-08**: accepted ticket writer authority; existing LADR-05 still owns memory-link derivation.
