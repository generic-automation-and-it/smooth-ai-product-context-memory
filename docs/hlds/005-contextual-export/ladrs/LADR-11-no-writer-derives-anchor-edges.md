# LADR-11: Capture-time derivation of anchor edges

**Status:** Blocked

> **Blocked by** — nothing in the system would write a ticket or tag edge even if the vertices existed.
> The capture skill derives relationships from cross-group subject matching between memories, and
> proposes them at the checkpoint. It has no step that inspects tickets or tags for relationships, no
> vocabulary for such an edge, and no way to propose one. The API persists what the skill decided, so it
> is not a candidate either.
>
> **Unblocking trigger** — LADR-09 or LADR-10 clearing first. A writer for edges whose vertices do not
> exist cannot be specified. Owner: the write-pipeline design (HLD-002), which owns link derivation.

## Context

LADR-09 and LADR-10 are blocked on *representation* — whether the graph may hold a ticket or tag vertex.
This LADR is the writer-side half of the same gap, and it is the half that is easy to overlook: adding a
vertex label is a migration, and it would produce an empty subgraph.

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

**Not taken.** Blocked on LADR-09 and LADR-10, and on the derive-versus-project question above.

The options, recorded so the first implementation does not invent one:

- **Extend capture-time link derivation** to propose ticket and tag edges alongside memory links. Fits the existing checkpoint and proposal model; creates a second copy of externally owned facts.
- **A separate reconciliation pass**, run deliberately, that reads tracker and vocabulary state and proposes edges in bulk. Keeps capture cheap; is a scheduled upkeep task, which `BR-01` treats as a failure signal.
- **Project at read time** from an authoritative source, storing nothing. No drift by construction; needs network access, which `BR-16` forbids depending on, so it would degrade rather than fail.
- **Practitioner-declared only** — no derivation at all; the practitioner states a ticket or tag relationship when it matters. Cheapest and most accurate; coverage will be thin, and thin coverage in a widening path produces the under-selection this HLD is trying to avoid.

**Interim behaviour, which ships:** no anchor edges are derived, proposed or written. Widening uses the
memory-to-memory edges capture already produces (LADR-03). Where the composition notices a relationship
that plainly should have been recorded, it is reported as a finding — which routes the gap back into the
normal capture path rather than around it (LADR-06).

## Consequences

- No migration adds a vertex label that would sit empty, and no writer is built for a schema that may not be approved.
- The findings become the evidence base for choosing among the four options — which relationships were actually missed, and how often.
- **Anchor-level widening stays unavailable for as long as this is blocked**, regardless of what LADR-09 and LADR-10 decide, because representation without a writer changes nothing.
- The derive-versus-project question is now recorded rather than being rediscovered during implementation.

## Related

- **LADR-09** and **LADR-10** — the representation half; both must clear before this can be specified.
- **LADR-06** — why a noticed missing relationship becomes a finding rather than a write.
- **HLD-002** — owns link derivation and therefore owns this decision when it unblocks.
