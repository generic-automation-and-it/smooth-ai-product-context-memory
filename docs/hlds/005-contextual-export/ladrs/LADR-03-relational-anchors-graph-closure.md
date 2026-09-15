# LADR-03: Anchors resolve relationally; widening runs on the graph, and its bound is on the wire

**Status:** Draft

## Context

`BR-18` selects by repository, initiative, ticket and tags. `BR-19` requires the selection to widen
along recorded relationships, and to state how far it went.

The two halves live in different stores. Repository and initiative are columns on a memory group;
tickets are documents on a group; tags and facets are arrays on a memory, matched by exact indexed
array predicates. Delivered relationships connect identity-only `Memory` vertices. HLD-003 LADR-08
now also has implemented and accepted identity-only Tickets and captured hierarchy. No tag
identity or synonym graph is approved.

## Decision

**Resolve** the anchor set relationally into a set of memory identities, then **widen** from those
identities over the graph, with a caller-supplied depth bound that has no server-side default.

This remains the memory-widening contract. The separate ticket traversal accepted in LADR-09 follows
only declared parent/child hierarchy, joins memories through live JSONB ownership, and returns current
non-proposed memories including the anchor's. It neither reinterprets memory `LINKS` nor silently
adds a history mode; the dossier history dimension below remains a separate selection concern.

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

- **Add every anchor as a vertex and do everything in Cypher**: not approved. LADR-09 resolves tickets only under HLD-003 LADR-08, with relational membership/owner joins. Tag identity and its writer remain blocked under LADRs 10/11; repository and initiative graphs are outside that approval.
- **Materialise the anchor sets into memories in the skill, then call the existing traversal endpoint per memory** — rejected: N traversals instead of one, no reproducibility guarantee, and selection logic that cannot be tested against the database.
- **Default the depth to 1 or 2** — rejected: HLD-003 LADR-07 already decided this, and an export is the case where an unexamined bound does the most damage.
- **Leave the combination rule to whatever the query composes** — rejected: it is then an unstated AND/OR choice that a reader can only infer from result size, which `BR-18` forbids. Either rule is defensible; leaving it implicit is not.
- **Broaden the criteria when nothing matches** — rejected: a helpful-looking widening returns material the practitioner did not ask for under a heading saying they did. `BR-18` requires the empty result.
- **Unbounded transitive closure with a size cap instead of a depth cap** — rejected: a size cap truncates by whatever the traversal reached first, which makes the result order-dependent and destroys reproducibility.

## Consequences

- `BR-18` and `BR-19` are satisfiable today with no schema change: relational anchors plus memory-to-memory widening.
- **Declared ticket parentage is implemented and accepted separately.** General ticket blockers, tag synonyms/parent topics and initiative graphs remain outside that contract. `ITicketGraph` composes a Cypher anchor with recursive SQL over AGE adjacency; selected capped path endpoint groups plus anchor supply memories. Final HLD-003 evidence closes the ticket performance gate; full dossier selection is not implemented by this traversal alone.
- Every widening request states its reach, so an export's completeness claim is always qualified.
- The caller must supply a depth bound. Slightly more friction on every request, deliberately.
- Selection cost is predictable from the bound, which is what makes the pre-composition manifest meaningful (NFR-03).
- The manifest grows: it now carries the combination rule and the history policy alongside the reach. That is the record `BR-20` needs to make an export repeatable, so it is not overhead.
- A related memory is included with **the reason it was added and its original scope** (`BR-19`), because a relationship does not make a rule applicable to another product or customer. Widening supplies candidates; applicability remains a property of the claim.

## Related

- **LADR-07** — ordering consumes the same edges this widening traverses.
- **LADR-09 / LADR-10 / LADR-11**: resolved ticket decisions and still-blocked tag prerequisites.
- **NFR-01** — every vertex crossed is scope-gated, not only the endpoints.
- **NFR-03** — the bound is the cost control.
