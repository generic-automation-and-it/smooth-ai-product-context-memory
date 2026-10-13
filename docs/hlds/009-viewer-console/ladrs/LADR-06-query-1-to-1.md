# LADR-06 — Browse filters map 1:1 onto the query API fields

## Context

`QueryMemories` executes every predicate in the database (full-text, array, validity indexes). Filtering
in the browser after materialising defeats the indexes and pulls whole version chains across the wire.

## Decision

The browse view maps its filter bar 1:1 onto the query API's declared fields: ticket (provider+key),
initiative, repo, facet (with any/all), tag, kind, status, group, free text, validity window. The SPA
translates the visible controls into the request; it never re-filters the returned rows client-side.

## Consequences

- Server indexes are used as designed; no client-side re-filter.
- The view is as correct as the API; no drift between the UI and the query semantics.
- Facet any/all is an explicit control, matching the API's `FacetMatchMode`.

## Status

Draft
