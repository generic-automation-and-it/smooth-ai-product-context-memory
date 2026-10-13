# LADR-02: `english` index/query pair preserved; operators use `to_tsquery` family

**Status:** Draft

## Context

The shipped pair is `to_tsvector('english', …)` on the index side
(`StemFullTextIndexes` migration) and `plainto_tsquery('english', …)` on the query side
(`NpgsqlMemorySearch.TextSearchConfig`). Changing one side silently drops the index
(`FreeTextRecallTests` pins this). HLD-001 NFR-02 selected `english` over `simple` and
rejected `pg_trgm` (figures under its stale-before-quoting banner — direction cited, not
numbers). Operators must not reopen that decision by accident.

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
`E_UNTERMINATED_QUOTE`. A query with no positive term (`-foo`, `NOT foo`, `-(a OR b)`
without a field predicate) is `E_NO_POSITIVE_TERM` — `to_tsquery('!foo')` is PG-valid
but matches the complement (silent widening). Complexity is bounded: max query length,
max terms/OR-arms, max paren depth, and min prefix length are fixed at Prototype with
`E_QUERY_TOO_COMPLEX`; single-char high-frequency prefixes (`a*`, `e*`) are the
adversarial case the perf harness must include. There is no fallback to
`plainto_tsquery` on the malformed path.

An interior hyphen is quoted as one lexeme (`'foo-bar'`), never stripped to `foobar` or
split. Native `tsquery` phrase/distance input (`a<->b`, `a<2>b`) is refused
(`E_DANGLING_OPERATOR`), never silently rewritten to AND. A non-field colon (`12:30`,
URLs) is refused (`E_UNKNOWN_FIELD`), never silently spaced — determinism requires one
arm. Prefix stopwords (`the:*`) are served as prefix (stopword removal does not apply
to `:*` terms) rather than refused; the earlier collapse example is corrected to bare
`the` only.

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
  translation — lexemes are quoted per `tsquery` quoting rules, operators are structural;
  binding alone does not stop operator injection, so both are required. Only
  Infrastructure serialises the query model to constructor calls: the composed SQL is
  `to_tsquery(@a) && phraseto_tsquery(@b)`-shaped (one expression tree, every fragment a
  bound parameter), never a single interpolated `to_tsquery('<built>')` string;
  `FromSqlRaw` concatenation of query text is forbidden and pinned by review, not just
  by the plan test. Intra-phrase per-word prefix (`down <-> ste:*`) is unsupported at
  Draft and refused; whole-`"phrase"*` is refused likewise.

## Related

- **LADR-06** — why prefix is enough and fuzzy stays out.
