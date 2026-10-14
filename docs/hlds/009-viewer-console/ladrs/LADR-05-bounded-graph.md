# LADR-05 — The graph view renders bounded traversal, not a graph DB

## Context

The store's graph is additive, not its home (HLD-003): the relational core is authoritative, and edges
exist only where paths are needed. Traversal is bounded (`/paths`, `/tickets/paths`, depth 1–5). It is
deliberately not a full knowledge-graph retrieval engine.

## Decision

The graph view renders what the store returns — typed links and ticket parent/child paths within the
declared traversal depth. It is a force-directed view of the selected element's neighbours, with a legend
for edge types. It never issues an unbounded walk, and it states its depth rather than implying a full
network.

## Consequences

- The view is honest about a shallow graph; it does not over-promise.
- Traversal stays bounded; no work proportional to the whole corpus.
- The graph is illustrative, not the authoritative read — detail/provenance stays the source of truth.

## Status

Draft
