# LADR-10: Tag anchors as graph vertices

**Status:** Blocked

> **Blocked by** — a tag has no representation in the graph, and no relationships anywhere. Tags and
> facets are arrays on a memory, matched by exact array predicates against GIN indexes. The label registry is
> deliberately non-enforcing and the usage view is derived, so vocabulary exists as *observed strings*
> rather than as entities. There is no tag identity to make a vertex from, and no tag-to-tag
> relationship recorded in any store.
>
> **Unblocking trigger** — two decisions, in order: (1) whether tag vocabulary becomes an entity with
> identity rather than an open string, owned by the label registry design; (2) whether the graph may hold
> a `Tag` vertex label, owned by HLD-003. Its LADR-08 supersedes LADR-02 for tickets only, not tags. The practical trigger is exports repeatedly
> reporting near-miss tags — a slice anchored on one tag that plainly should have included a
> near-synonym.

## Context

`BR-18` selects by tags, and that works today: exact array matching resolves a tag anchor to a set of
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
- **Let the skill report evidence-backed near-miss tags.** This can expose a limitation without widening or requiring the full dossier implementation; it cannot establish missing records from unseen material.

**Interim decision, owner-approved 2026-09-14; helper implemented:** keep exact tag selection
unchanged (ordinary retrieval defaults to indexed `any` overlap; `all` containment remains explicit).
Only matched memories supply memory-graph widening. Report that tag relationships were not followed.

The skill may report **`near-miss-tag`** only from evidence already present in the authorized examined
material, including tags or claims within that material that support a vocabulary mismatch. Every
finding carries supporting memory **UUID/version**, its concrete **basis**, examined **scope**, and
**classification** as observation or analysis (LADR-13). Tag spellings actually present can be
observations; proposed synonymy, relevance or likely under-selection is analysis, not an established
synonym or a claim that an unseen record was excluded. If no such evidence exists, emit no finding.

This is **evidence-only skill reporting**: no synonym query, candidate search, registry scan, relaxed
filter, extra traversal or search broadening to find a near miss. No hidden memory identifiers or
counts, invented UUIDs, tag registration, graph writes or automatic recapture. A no-match stays a
no-match. Findings do not add memories to the selected set or establish store-wide absence.

[NFR-04](../nfrs/NFR-04-completeness.md) adds `near-miss-tag` to the bounded taxonomy and fixes its
evidence checks. This narrow reporting decision does not unblock tag identity/synonyms or approve
the full dossier implementation.

## Consequences

The executable [near_miss_tags.py](../../../../.agents/skills/mimisbrunnr-context-memory/scripts/near_miss_tags.py)
is an offline stdin/stdout validator and reporter, not a semantic detector. Its strict input contains
approved UUID/version references with per-record `scopeDimension`/`scopeIdentifier`, the unchanged
`originalQuery` using API field `facetMatchMode` (`any` when absent), examined records carrying
`status` and their matching approved scope, caller/skill relevance analysis
with an exact quote from the referenced statement, selected references and disclosure. A finding
requires both relevant analysis and a failed exact `any`/`all` tag predicate. The mismatch is an
observation; relevance remains analysis. Findings retain each record's status and scope and explicitly
flag `proposedEvidence`; permission to examine proposed evidence does not make it approved knowledge.
The examined-set name and query scope never replace record applicability. The helper uses this current
schema, not separate `criteria`/`tagsMatchMode` fields. It preserves `originalQuery`, selection and
disclosure, sorts by UUID/version,
and rejects over 1 MiB, 200 records/analyses/references or 200 tags per list without partial output.
These are helper safety limits, not dossier-wide caps. Tests cover deterministic plumbing and no
extra I/O; they do not prove authorization truth, semantic relevance quality or full dossier readiness.

- `BR-18` is satisfied as written, with the same qualified completeness claim as the ticket case.
- **A slice anchored on a tag under-selects whenever the vocabulary drifted.** Given an open vocabulary and a single practitioner typing tags over months, this will happen and will be the more frequent of the two under-selection failures.
- Evidence-backed near-miss findings can inform whether vocabulary needs identity, but a slice cannot
  prove an unseen synonym or excluded memory exists. Coverage claims remain qualified.
- The open-vocabulary decision stays untouched, so capture is never blocked on registering a word.
- No schema is added that a later vocabulary decision would have to undo.

## Related

- **LADR-03** — the interim behaviour this leaves in place.
- **LADR-09**: ticket representation is resolved under HLD-003 LADR-08; no corresponding tag approval.
- **LADR-11**: ticket writer resolved; tag writer remains blocked.
- **NFR-02** — why the similarity option is unattractive.
