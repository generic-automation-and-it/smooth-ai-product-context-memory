# LADR-03: Anchors resolve relationally; widening runs on the graph, and its bound is on the wire

**Status:** Draft

## Context

`BR-18` selects by repository, initiative, ticket and tags. `BR-19` requires the selection to widen
along recorded relationships, and to state how far it went.

The two halves live in different stores. Repository and initiative are columns on a memory group;
tickets are documents on a group; tags and facets are arrays on a memory, matched by array containment
against GIN indexes. Relationships live in the graph, where the only vertex label is `Memory` and it
carries identity and nothing else.

So there is no single query language for both halves, and no vertex to start a traversal from when the
anchor is a ticket or a tag.

## Decision

**Resolve** the anchor set relationally into a set of memory identities, then **widen** from those
identities over the graph, with a caller-supplied depth bound that has no server-side default.

**Combination semantics are stated, not assumed.** `BR-18` requires combined criteria to have defined,
visible behaviour, so this design fixes it rather than letting a query builder imply it: **values within
one category are alternatives (any), and categories are conjunctive (all)** — repository *and*
initiative *and* ticket *and* tags, where a list of tags matches any of them. That rule is reported in
the manifest with the resolved criteria, so a reader never has to infer it from the result size. A
request matching nothing is reported as no match; the selection is **never silently broadened** to
produce something, and an ambiguous identifier is raised for clarification rather than guessed.

**History is a selection dimension, not a composition afterthought.** `BR-24` requires selected
superseded and no-longer-true positions to remain readable with their recorded reasoning, so whether
history is included is part of the request, part of the recorded effective selection, and part of what
the preview prices. Defaulting it silently either hides the reasoning `BR-24` protects or doubles the
cost of every export without saying so.

Anchor resolution is ordinary retrieval: the existing search abstraction already translates repository,
ticket, facet and tag predicates into one statement that uses the indexes built for them. Widening is
ordinary traversal: bounded, relation-filterable, and gated at every vertex it crosses. Because SQL and
Cypher share a session, the two halves compose into one round trip rather than an application-side
join.

The depth bound is required on the request and is not defaulted, for the reason HLD-003 already
records: **a default is a bound the caller never considered**. An export's reach determines both its
cost and its completeness claim, so it is the last place to inherit an unexamined number.

The bound, the relations followed, and everything the widening stopped short of are reported in the
manifest. An unstated reach is indistinguishable from completeness, which is the failure `BR-19` and
`BR-30` exist to prevent.

## Alternatives Considered

- **Add ticket, tag, repository and initiative vertices and do everything in Cypher** — not rejected on merit; **blocked**. See LADR-09, LADR-10 and LADR-11: the thin-vertex rule constrains it, and nothing would write the edges. Revisit there, not here.
- **Materialise the anchor sets into memories in the skill, then call the existing traversal endpoint per memory** — rejected: N traversals instead of one, no reproducibility guarantee, and selection logic that cannot be tested against the database.
- **Default the depth to 1 or 2** — rejected: HLD-003 LADR-07 already decided this, and an export is the case where an unexamined bound does the most damage.
- **Leave the combination rule to whatever the query composes** — rejected: it is then an unstated AND/OR choice that a reader can only infer from result size, which `BR-18` forbids. Either rule is defensible; leaving it implicit is not.
- **Broaden the criteria when nothing matches** — rejected: a helpful-looking widening returns material the practitioner did not ask for under a heading saying they did. `BR-18` requires the empty result.
- **Unbounded transitive closure with a size cap instead of a depth cap** — rejected: a size cap truncates by whatever the traversal reached first, which makes the result order-dependent and destroys reproducibility.

## Consequences

- `BR-18` and `BR-19` are satisfiable today with no schema change: relational anchors plus memory-to-memory widening.
- **Relationships between anchors cannot be expressed or followed.** A ticket's blockers, a tag's parent topic, an initiative's tickets — none of these are traversable. This is a real functional limit, named in LADR-09 and LADR-10 rather than worked around.
- Every widening request states its reach, so an export's completeness claim is always qualified.
- The caller must supply a depth bound. Slightly more friction on every request, deliberately.
- Selection cost is predictable from the bound, which is what makes the pre-composition manifest meaningful (NFR-03).
- The manifest grows: it now carries the combination rule and the history policy alongside the reach. That is the record `BR-20` needs to make an export repeatable, so it is not overhead.
- A related memory is included with **the reason it was added and its original scope** (`BR-19`), because a relationship does not make a rule applicable to another product or customer. Widening supplies candidates; applicability remains a property of the claim.

## Related

- **LADR-07** — ordering consumes the same edges this widening traverses.
- **LADR-09 / LADR-10 / LADR-11** — the blocked prerequisites for anchor-level traversal.
- **NFR-01** — every vertex crossed is scope-gated, not only the endpoints.
- **NFR-03** — the bound is the cost control.
