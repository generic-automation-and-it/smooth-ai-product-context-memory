# LADR-03: `field:value` sugar maps onto existing criteria, conjunctively

**Status:** Draft

## Context

`MemorySearchCriteria` already carries `Facets`, `Tags`, `Kind`, `Repo`, `InitiativeName`,
`Status`, `GroupUuid` — the structured equivalents of Obsidian's `tag:`/`path:`/`file:`.
Two ways to expose them in a string: new FTS predicates over those columns, or mapping onto
the existing fields. New predicates would duplicate the GIN array-operator path and the
scope/lifecycle guards for no gain.

## Decision

**Map** the documented `field:value` forms onto the existing criteria fields and combine
them conjunctively (`AND`) with the FTS match and with every resolved filter (scope plan,
`ExcludeProposed`, `CurrentOnly`, `AsOf`). The translator emits row-identical results to
the equivalent structured request (set equality, not SQL-text identity — LINQ aliasing
differs). The supported set is closed: `tag`, `facet`, `kind`, `repo`, `initiative`,
`status`, `group`. Anything else is a validation refusal (`E_UNKNOWN_FIELD`).

`:` is structural only as `^(tag|facet|kind|repo|initiative|status|group):` with an
immediate bare or `"quoted"` value and no space before the colon. Everything else
(`12:30`, URLs, `foo:bar`, spaced colons, multi-colons) sanitises the colon to a space or
refuses with `E_UNKNOWN_FIELD` / `E_EMPTY_VALUE` — never a partial-field match. Field
names are lowercase-only; `TAG:x` is `E_UNKNOWN_FIELD`. `tag : foo` is refused, not folded.

A field-only query (≥ 1 field, zero FTS lexemes) is valid: the translator emits criteria
predicates with no `tsquery` predicate, never `plainto_tsquery('')` (live: empty yields
`''`, which matches nothing — omitting is correct). A stopword-collapse
(`the`, `"to be"`, `the*` → empty after `english` normalisation) with ≥ 1 field degrades
to field-only; without fields it refuses (`E_EMPTY_AFTER_NORMALIZATION`). A query with no
positive term (`-foo`, `NOT foo`, `-(a OR b)` without a field predicate) refuses
(`E_NO_POSITIVE_TERM`) — `to_tsquery('!foo')` is PG-valid (live: `!'foo'`) but matches
the complement (silent widening).

## Alternatives Considered

- **Fold fields into the `tsquery`** (e.g. index tags into the tsvector) — rejected:
  breaks the unversioned-classification placement (HLD-001 LADR-03: tags/facets live on the
  stable row deliberately) and defeats the array GIN indexes.
- **Silently ignore unknown fields** — rejected: Obsidian-compatible tooling would assume
  narrowing that never happened; refusal is the honest response.

## Consequences

- No new column, index, or filter semantic; lifecycle negative controls keep passing unchanged.
- Repeat rule by cardinality: single-valued (`status`, `kind`, `repo`, `initiative`,
  `group`) — any repeat refuses (`E_DUPLICATE_FIELD`), even identical. Multi-valued
  (`tag`, `facet`) — repeats accumulate per `FacetMatchMode` (default `Any`/OR);
  identical values dedupe pre-translation. Cross-field conflicts (e.g.
  `status:proposed` plus an explicit include-proposed flag) resolve per the lifecycle
  controls, never silently.
- Value rule: `status:` is closed to `{proposed, approved}`, else
  `E_INVALID_FIELD_VALUE`. `kind:` is open vocabulary — any non-empty value passes as an
  exact match. `group:` is a strict uuid parse, else `E_INVALID_FIELD_VALUE`.
  `tag`/`facet`/`repo`/`initiative` must be non-empty after trimming quotes, else
  `E_EMPTY_VALUE`.
- Refusal reasons are a closed enum and carry the field *name* at most — never the field
  value, lexeme, phrase, or snippet (NFR-04).
- Quoted multiword values (`repo:"my repo"`) need the same tokenizer as FTS lexemes;
  LADR-01's parser owns that.

## Open

- `group:` accepts uuid spelling only at Draft; human-name resolution is TBD (owner:
  generik0 — trigger: Prototype if requested).
