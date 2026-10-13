# LADR-02: `english` index/query pair preserved; operators use `to_tsquery` family

**Status:** Draft

## Context

The shipped pair is `to_tsvector('english', …)` on the index side
(`StemFullTextIndexes` migration) and `plainto_tsquery('english', …)` on the query side
(`NpgsqlMemorySearch.TextSearchConfig`). Changing one side silently drops the index
(`FreeTextRecallTests` pins this). HLD-001 NFR-02 measured `english` at recall 0.78 /
precision 0.88 and rejected `pg_trgm`. Operators must not reopen that decision by accident.

## Decision

**Preserve** the `english` configuration on both sides. Bare words keep
`plainto_tsquery`; explicit operators translate to the matching `to_tsquery`-family
constructor in the same configuration: `OR` → `|`, `AND`/implicit → `&`, `-`/`NOT` → `!`,
`"phrase"` → `phraseto_tsquery`, trailing `*` → `:*` prefix. The configuration string stays
a single constant shared by every branch.

Precedence (tightest first): `( )` grouping, phrase/prefix atom, unary `-`/`NOT`
(right-associative), explicit `AND` and implicit-AND, then `OR` (left-associative). So
`a OR b c` is `a | (b & c)` and `NOT a OR b` is `(!a) | b`. `OR`/`AND`/`NOT` are
uppercase-only reserved words; lowercase `or`/`and`/`not` are lexemes. A reserved word in
quotes (`"OR"`) is a literal lexeme, never an operator.

`-` is `NOT` only in token-start position (string start, whitespace, or `(`) directly
attached to an atom (`-x`, `-(a b)`). An interior hyphen (`foo-bar`, `well-known`) is a
literal lexeme, sanitised, never split. `-` followed by space, `--x`, `-*`, `*foo`,
`foo**`, `**`, `(*)`, `"*"`, and `"phrase"*` are refusals (`E_DANGLING_OPERATOR` /
`E_LONE_PREFIX`), never silent drops. `()` and `( )` are `E_EMPTY_GROUP`; leading,
trailing, or doubled operators (`OR a`, `a OR`, `a OR OR b`, lone `NOT`/`-`, `a AND`)
are `E_DANGLING_OPERATOR`; unbalanced paren/quote are `E_UNBALANCED_PAREN` /
`E_UNTERMINATED_QUOTE`. There is no fallback to `plainto_tsquery` on the malformed path.

## Alternatives Considered

- **`websearch_to_tsquery` as the whole syntax** — rejected: bakes in Google-style
  semantics (quoted phrase, `-`, `OR`) but removes grouping control and field sugar, and
  hides the constructor choice the plan test must pin.
- **Per-branch configuration** — rejected: the exact silent-decoupling failure the plan
  test exists to catch.

## Consequences

- Stemming behaviour (including the pinned `author` collision) is unchanged; operators add
  expressiveness, not a new matching regime.
- `:*` prefix is index-served by the same GIN index; no new index.
- Parser must sanitise the full metachar set (`& | ! ( ) : * " \ ' < > - /`) before
  translation — lexemes are quoted, operators are structural; confusing the two is an
  injection-shaped defect even with parameters. The translator calls
  `to_tsquery(config, @param)` with the built query as a bound parameter, never string
  interpolation; `FromSqlRaw` concatenation of query text is forbidden and pinned by
  review, not just by the plan test.

## Related

- **LADR-06** — why prefix is enough and fuzzy stays out.
