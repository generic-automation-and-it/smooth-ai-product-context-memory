# LADR-04: `ts_rank_cd` orders, `ts_headline` shows evidence

**Status:** Draft

## Context

Today's order is `ValidFrom desc, uuid, version` — recency, not relevance. A two-lexeme
exact hit can sort below a one-lexeme stem hit that happens to be newer, and the caller
gets the full cheap row with no indication of which side matched. Obsidian answers this
with relevance sort plus highlighted context; the skill's semantic judgement needs the
same evidence without pulling blob bodies.

## Decision

**Order** by query kind: bare-text queries (including field-only and field+bare with
zero explicit operators — no `tsquery` predicate, nothing to rank) keep `ORDER BY
ValidFrom desc, uuid ASC,
version ASC` with no rank. Operator queries (any explicit `|`/`&`/`!`/phrase/` :*`
constructor from LADR-02) order by `ts_rank_cd(vector, query) DESC` with the same
recency tuple as the deterministic tiebreak, and **project** a nullable `snippet`
(`ts_headline('english', …)` over the matched side, subject vs. claim). Rank input is
the same `tsquery` from LADR-02 — no second signal, no stored rank column. Rank uses
default weights (index lexemes all carry the default weight D — uniform, no `setweight`)
and default normalization 0 (`{0.1, 0.2, 0.4, 1.0}` document-length weights apply as-is);
both-match (subject + claim) takes the max side. `snippet` options are fixed
(`MaxFragments=1, MaxWords=35, MinWords=15, ShortWord=3, FragmentDelimiter=' ... '` —
PG defaults, caller-untunable): 1 fragment × 35 words ≈ ≤ ~300 B/row × 200 rows ≈ ≤
~60 KB worst case, post-`LIMIT` only — the bound behind the NFR-02 ≤2× claim.
`CheapMemory` carries it as a trailing optional ctor param (`string? Snippet = null`,
last position — existing construction sites compile untouched). Existing columns and
projection order are unchanged; `snippet` (camelCase, always present, nullable) is
additive — null when the headline marks nothing (`ts_headline` returns unmarked text,
which the translator maps to null). Headline input columns are pinned to the
subject/claim cheap fields — never blob bodies.

## Alternatives Considered

- **Stored rank / popularity column** — rejected: usage counts are a derived view by
  decision (HLD-001); writing one back reintroduces the write-amplification the design
  removed.
- **Caller-tunable headline options** — rejected: option strings approach SQL text; fixed
  options keep values parameterised and output stable for byte-equality surfaces.

## Consequences

- Ordering changes on operator queries only; bare order is unchanged; the result *set*
  for a fixed query never changes with rank.
- `ts_headline` runs per returned row only (post-`LIMIT`), so its cost is bounded by the
  existing 50/200 ceiling.
- `snippet` text is memory content: it inherits the no-content-in-logs rule (HLD-001) and is
  excluded from recall-feedback payloads (identifiers only).

## Related

- **NFR-02** — bounds the added rank/headline cost.
