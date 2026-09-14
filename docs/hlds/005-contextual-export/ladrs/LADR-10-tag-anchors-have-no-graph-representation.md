# LADR-10: Tag anchors as graph vertices

**Status:** Blocked

> **Blocked by** — a tag has no representation in the graph, and no relationships anywhere. Tags and
> facets are arrays on a memory, matched by array containment against GIN indexes. The label registry is
> deliberately non-enforcing and the usage view is derived, so vocabulary exists as *observed strings*
> rather than as entities. There is no tag identity to make a vertex from, and no tag-to-tag
> relationship recorded in any store.
>
> **Unblocking trigger** — two decisions, in order: (1) whether tag vocabulary becomes an entity with
> identity rather than an open string, owned by the label registry design; (2) whether the graph may hold
> a non-`Memory` vertex label, owned by HLD-003 LADR-02. The practical trigger is exports repeatedly
> reporting near-miss tags — a slice anchored on one tag that plainly should have included a
> near-synonym.

## Context

`BR-18` selects by tags, and that works today: array containment resolves a tag anchor to a set of
memories, and widening proceeds from those memories (LADR-03).

What does not exist is any notion of one tag relating to another. `postgres` and `database`,
`observability` and `telemetry`, a broad topic and its narrower children — the store has no way to know
these are connected, so a slice anchored on one tag silently excludes material tagged with its synonym.
`kind` is an open string by decision, and tags are the same: the vocabulary is whatever was typed, and
the registry records proposals without enforcing them.

This is a harder block than the ticket case. A ticket at least has an identity issued elsewhere — a
tracker and a key. A tag has nothing: two spellings of one idea are two unrelated strings, and making
them relatable means first deciding that tag vocabulary has identity at all. That is a change to the
open-vocabulary decision, not just a graph addition.

The open vocabulary is not an accident either. It exists so capture is never blocked on registering a
word, and the usage view exists to surface drift precisely because the registry does not enforce.
Closing the vocabulary to enable traversal would trade a capture-time freedom for a retrieval-time gain,
and that trade has not been made.

## Decision

**Not taken.** Two upstream decisions must land first, and neither belongs to this HLD.

The options, recorded so the first implementation does not invent one:

- **A tag vertex label** with edges for synonym, broader and narrower. Requires tag identity, so requires closing or at least anchoring the vocabulary.
- **Tag relationships in the label registry**, joined at selection time rather than traversed. Cheapest, keeps the graph pure, and gives up variable-depth tag hierarchies — which may be entirely sufficient, since synonym and one-level-broader is most of the value.
- **Similarity at selection time** — expand a tag anchor to statistically co-occurring tags. No vocabulary decision needed and no schema change; deeply non-deterministic, which collides with NFR-02.
- **Let the composition notice missing tags and report them as findings.** Not a substitute for widening, but it does turn the limitation into a signal — and it needs nothing built.

**Interim behaviour, which ships:** a tag anchor matches by containment, exactly as ordinary retrieval
does. Widening proceeds from the matched memories. The manifest states that tag relationships were not
followed. Where the composition notices that a near-synonym tag was plainly relevant and excluded, that
is reported as a finding under `BR-29`, which is the last option above and costs nothing.

## Consequences

- `BR-18` is satisfied as written, with the same qualified completeness claim as the ticket case.
- **A slice anchored on a tag under-selects whenever the vocabulary drifted.** Given an open vocabulary and a single practitioner typing tags over months, this will happen and will be the more frequent of the two under-selection failures.
- Reporting near-miss tags as findings makes the gap self-documenting, and the findings themselves become the evidence for whether the vocabulary needs identity at all.
- The open-vocabulary decision stays untouched, so capture is never blocked on registering a word.
- No schema is added that a later vocabulary decision would have to undo.

## Related

- **LADR-03** — the interim behaviour this leaves in place.
- **LADR-09** — the same block, for tickets, with an easier identity story.
- **LADR-11** — even with the vertices, nothing would write the edges.
- **NFR-02** — why the similarity option is unattractive.
