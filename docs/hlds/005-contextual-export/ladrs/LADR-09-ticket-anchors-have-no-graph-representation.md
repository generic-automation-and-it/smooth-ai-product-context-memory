# LADR-09: Ticket anchors as graph vertices

**Status:** Blocked

> **Blocked by** — a ticket has no representation in the graph. The graph declares exactly one vertex
> label, `Memory`, and HLD-003 LADR-02 constrains a vertex to a label plus the memory's stable identity,
> with no descriptive property permitted. Tickets are documents on a memory group in the relational
> store. There is nothing to traverse from and nothing to traverse to.
>
> **Unblocking trigger** — a decision on whether the graph may hold a non-`Memory` vertex label, taken
> against HLD-003 LADR-02 rather than around it. Owner: HLD-003. The trigger is the first export whose
> findings show that ticket-to-ticket relationships were the missing information, not a hypothetical
> need.

## Context

`BR-18` selects by ticket, and that works today: a ticket resolves relationally to a memory group, and
the group's memories become the anchor set (LADR-03). What does **not** work is any relationship *between
tickets*.

A ticket's blockers, the tickets an initiative contains, the ticket a ticket was split from, the ticket
that supersedes another — these are real relationships in the practitioner's work, tracked in whatever
issue tracker issued them, and completely absent from the store's graph. A slice anchored on a ticket
therefore cannot widen to "and everything about the tickets this one depends on", even though that is
frequently the shape of the question.

The block is not an oversight. HLD-003 LADR-02 rejected descriptive properties on vertices because
every polyglot failure it surveyed failed the same way: one fact in two stores, drifting, with no
arbiter. A ticket vertex is that argument's hardest case — ticket identity is owned by an external
tracker, mirrored into a group's documents, and would then exist a third time in the graph.

## Decision

**Not taken.** The options cannot be evaluated until the thin-vertex rule is either upheld or amended,
and that decision belongs to HLD-003.

The options, recorded so the first implementation does not invent one:

- **A ticket vertex label** carrying tracker and key as identity, edged to the memories in its group and to other tickets. Directly contradicts the thin-vertex rule unless "identity" is read to include an external key.
- **Ticket relationships as relational rows**, joined rather than traversed. Keeps the graph pure and gives up variable-depth ticket chains.
- **Ticket relationships derived from the tracker at export time**, never stored. Accurate and fresh, but requires network access, which `BR-16` forbids depending on.
- **Ticket relationships projected onto memory edges** — if ticket A blocks ticket B, edge every A-memory to every B-memory. Rejected on sight in this list: it manufactures edges nobody recorded and would corrupt both ordering (LADR-07) and contradiction detection (LADR-04).

**Interim behaviour, which ships:** a ticket anchor resolves relationally to its group's memories, and
widening proceeds from those memories over recorded memory-to-memory edges. The manifest states that
ticket-level relationships were not followed, so the export's completeness claim is qualified rather than
overstated (`BR-19`, `BR-30`).

## Consequences

- `BR-18` and `BR-19` are satisfied as written; the limitation is one the requirements do not currently demand be lifted.
- **A slice anchored on a ticket under-selects whenever the answer lives in a related ticket.** This is the most likely source of a "the export missed the obvious thing" complaint, and it will be reported honestly rather than silently.
- Nothing is built that would have to be unbuilt when the block clears.
- The manifest gains a permanent "not followed" entry, which is accurate and also a standing reminder that this LADR is open.

## Related

- **LADR-03** — the interim behaviour this leaves in place.
- **LADR-10** — the same block, for tags.
- **LADR-11** — even with the vertices, nothing would write the edges.
- **HLD-003 LADR-02** — the rule that constrains every option here.
