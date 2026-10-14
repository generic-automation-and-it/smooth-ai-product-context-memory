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
(string → `And`/`Or`/`Not`/`Phrase`/`Prefix`/`Term` operator tree plus criteria; the case
names are syntactic and carry no `to_tsquery` spelling — only Infrastructure knows
`|`/`&`/`!`/`:*`); Infrastructure owns the translation (query model →
`to_tsquery` / `phraseto_tsquery` / `plainto_tsquery` expression tree, executed by the
database). All fourteen `E_*` syntax refusals live in the parser inside the handler (not in
the FluentValidation validator — the validator keeps today's shape checks; the handler
maps parser refusal to 400 itself), so refusals never emit HLD-004 recall-feedback (a
refusal is not a miss — misses are empty retrieval results only). `E_EMPTY_AFTER_NORMALIZATION`
is detected at translation time (only the provider knows `english` stemming): the
translator returns the refusal without hitting the tables. Tokenizer (separators incl.
NBSP/tab/newline, `,`/`.`/`/`/`@` splitting, NFC normalisation, ASCII `"` only — smart
quotes are lexeme chars) and field-vs-operator precedence (`tag:x` binds as an atom:
`tag:x foo OR bar` is `(field & foo) | bar`) are pinned at Prototype with the L0/NFR-01
fixture. L0 asserts the operator tree; a separate Infrastructure fixture asserts the
`tsquery` text — one layer per fixture. New retrieval shapes map onto the HLD-004 shape
set explicitly (operator query → `free_text`, field-only → `facet_only` or matching
scoped shape, traverse → its anchor's shape; CHECK gains no new value without an
HLD-004 amendment), classified by a pure overload
`RecallShapeClassifier.Classify(ParsedKeywordQuery parsed, bool hasTraverse)` on the
Application-owned parse result (operator tree + field map + `HasExplicitOperator`) —
the `Request`-only overload delegates with `hasOperators: false`, and the handler calls
post-parse. The seam itself is extended, not split: `MemorySearchCriteria` gains a
nullable `KeywordQuery StructuredQuery = null` (null = bare path, zero churn for the
handler, dossier, and ticket-graph callers) behind the single `SearchAsync`; no second
`IMemorySearch` method forks the GIN predicate plumbing.

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

- Query-model serialisation review at first translator draft (owner: generik0): confirm no
  `to_tsquery` spelling leaks into the Application tree.
- CJK/non-English limitation disclosed at Prototype (owner: generik0): `english` drops
  CJK tokens — pin whether refusal, empty set, or documented limitation.

## Related

- **LADR-02** — constrains which `tsquery` constructors the translator may use.
- **LADR-03** — field sugar rides the same parser, maps to criteria, not to `tsquery`.
