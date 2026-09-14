# LADR-07: Ordering is a deterministic topological sort over provenance relations

**Status:** Draft

## Context

`BR-22` requires a decision and the reasoning it rests on to appear in a followable order. The store
holds the material for that: relations such as `depends_on`, `supersedes` and `implements` describe
which memory rests on which, and each edge carries the reason it was recorded.

Two things make this harder than a sort. The relation vocabulary is deliberately **open** — unknown
relations persist and are traversable — so the ordering cannot assume a closed set. And the graph has
no acyclicity guarantee; nothing prevents a cycle, and reciprocal `relates_to` edges are entirely
normal.

Ordering is also the one part of composition that could plausibly be deterministic, and pinning it down
is what stops "the model ordered it somehow" from becoming the design.

## Decision

**Order** by topological sort over a named, ordered subset of provenance relations, with a
deterministic tiebreak, and cycles broken by a stated rule rather than by traversal accident.

The relations that carry ordering meaning are named explicitly — `supersedes`, `depends_on`,
`implements` — and the ordering ignores the rest. `relates_to` and unknown relations connect material
without ordering it, which is correct: they say two things are related, not that one precedes the other.
An open vocabulary therefore extends the graph without silently changing document order.

Where the sort leaves items unordered relative to each other, the tiebreak is stated and stable —
business-time validity, then capture time, then memory identity — so two runs over one bundle produce
one order. Where a cycle exists, it is broken at a stated point and the cycle is **reported as a
finding**: a cycle in provenance means the store records that A rests on B and B on A, which is
information about capture quality rather than a rendering inconvenience.

The sort itself is mechanical, so it can be specified and tested. What the composition then does with
the ordered material — grouping, sectioning, narrative connective tissue — remains judgement.

## Alternatives Considered

- **Chronological by capture time** — rejected: capture order is arrival order, which is what `BR-22` explicitly rejects. Reasoning is frequently captured after the decision it justifies.
- **Let the model order the material freely** — rejected: makes document order unexplainable and untestable, and the one part of composition that can be pinned down would not be.
- **Include every relation in the sort, including `relates_to`** — rejected: reciprocal `relates_to` edges are normal, so this would manufacture cycles constantly and the cycle-breaking rule would dominate the ordering.
- **Break cycles silently by dropping the edge that closes them** — rejected: hides a genuine capture defect, and which edge closes a cycle depends on traversal order, which would break reproducibility.

## Consequences

- Document order is explainable from the edges, and two runs over one bundle agree.
- Adding a new relation to the open vocabulary does not change the order of existing documents.
- A provenance cycle becomes a finding, adding a category to the findings taxonomy that is genuinely actionable.
- Material connected only by `relates_to` gets no ordering help and falls to the tiebreak, so a slice held together entirely by loose relations reads as a flat list. Accepted: that is an accurate depiction of what the store holds.

## Related

- **LADR-03** — the widening that supplies these edges.
- **LADR-05** — collapse happens after ordering, on already-ordered material.
- **NFR-02** — the tiebreak exists so the ordered bundle is byte-identical between runs.
