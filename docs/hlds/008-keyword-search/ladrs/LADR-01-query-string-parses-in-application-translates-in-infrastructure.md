# LADR-01: Query string parses in Application, translates in Infrastructure

**Status:** Draft

## Context

`IMemorySearch` exists because matching the full-text and array indexes needs
provider-specific operators (`to_tsvector`, `&&`/`@>`) that Application must not reference
(`FEATURES_AGENTS.md`). A keyword syntax adds a second provider-sensitive surface: turning
`OR`, `-`, `"phrase"`, `*` into a `tsquery`. Putting that parse next to the SQL invites the
old defect — handlers reaching for provider syntax and filtering in memory.

## Decision

**Split** the work at the existing seam: Application owns a pure, total parser
(string → operator tree → criteria plus a provider-neutral query model); Infrastructure owns
the translation (query model → `to_tsquery` / `phraseto_tsquery` / `plainto_tsquery`
expression tree, executed by the database). The handler resolves group/ticket identity and
the scope plan before either runs, exactly as `QueryMemories.Handler` does today.

## Alternatives Considered

- **Parse in Infrastructure next to the SQL** — rejected: couples deterministic syntax to the
  provider, untestable without a database, repeats the defect the abstraction was built to end.
- **Parse client-side (read client builds `tsquery`)** — rejected: caller-supplied fragments
  approach statement text; the server loses its validation refusal point.

## Consequences

- Parser is L0-testable with no database; translation stays EXPLAIN-verifiable.
- In-process callers bypassing the handler still cannot smuggle provider syntax — they hand
  the same criteria model.
- Two places to change for one feature (parser + translator); the seam is the cost of the
  existing layering.

## Open

- Query-model shape (closed discriminated union vs. stringly intermediate): owner generik0
  at Prototype — trigger: first translator draft.

## Related

- **LADR-02** — constrains which `tsquery` constructors the translator may use.
- **LADR-03** — field sugar rides the same parser, maps to criteria, not to `tsquery`.
