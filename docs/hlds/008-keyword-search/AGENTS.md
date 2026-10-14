# AGENTS.md - Keyword Search

AI Context: HLD for Keyword Search. Updated: 2026-10-13

> AI-coder context for this HLD. Architecture diagrams live in [`./diagrams/`](./diagrams/),
> decisions in [`./ladrs/`](./ladrs/), quality spec in [`./nfrs/`](./nfrs/). This file is
> guardrails, not narrative — the narrative is in [`./README.md`](./README.md).

## TL;DR

Obsidian-like keyword syntax over the shipped `english` FTS pair (phrase/OR/NOT/prefix/field sugar + rank/snippets + optional traverse); intent in README, decisions in ladrs/, quality bar in nfrs/.

## Non-Negotiables

- Do not collapse parsing into Infrastructure — the Application/Infrastructure split is LADR-01; provider syntax in a handler repeats the defect `IMemorySearch` was built to end.
- Do not change the `english` index/query configuration on one side only — the pair silently decouples and the index stops serving (LADR-02).
- Do not add a new filter predicate for `field:value` — map onto existing criteria conjunctively (LADR-03); unknown fields refuse, never ignore.
- Do not let rank filter — `ts_rank_cd` orders only; the set is fixed before ranking (LADR-04).
- Do not add `pg_trgm`, vectors, or server-side semantic judgement — refused by LADR-06 and `what-is-special` #2/#5.
- Never take memory content (statement, summary, snippet, query lexemes) into a log, span, or feedback payload — identifiers and counts only.
- LADRs are Draft — flag deviations rather than silently overriding.

## Architecture Decisions

Only decisions whose violation produces wrong code. Full records in [`./ladrs/`](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| [LADR-01](./ladrs/LADR-01-query-string-parses-in-application-translates-in-infrastructure.md) | Parse in Application, translate in Infrastructure | Provider syntax in handler → in-memory filtering, index defeat |
| [LADR-02](./ladrs/LADR-02-english-pair-preserved-operators-use-totsquery.md) | Keep `english` pair; operators use `to_tsquery` family | One-sided change silently drops the GIN index |
| [LADR-03](./ladrs/LADR-03-field-sugar-maps-onto-existing-criteria-conjunctively.md) | Field sugar maps onto existing criteria, AND-combined | New predicates duplicate GIN path + bypass lifecycle guards |
| [LADR-04](./ladrs/LADR-04-rank-cd-orders-headline-shows-evidence.md) | `ts_rank_cd` orders, `ts_headline` post-LIMIT | Rank-as-filter or pre-LIMIT headline changes sets and cost |
| [LADR-05](./ladrs/LADR-05-keyword-plus-traverse-composes-in-one-round-trip.md) | Keyword + bounded traverse in one round trip, scope-pushed | Client truncation before traverse + selector leak |
| [LADR-06](./ladrs/LADR-06-fuzzy-stays-rejected-prefix-covers-typeahead.md) | Fuzzy stays rejected; `:*` covers prefix | Extension + threshold semantics for no measured gain |

## Key Behaviors

- Bare keeps recency with no rank; operator queries rank with recency tiebreak — the
  branch key is any explicit operator (LADR-02 ctors).
- `snippet` is additive and nullable; existing columns and projection order never move.
- Lexemes are quoted, operators structural: sanitise the full metachar set and bind the
  built query as a parameter — never interpolate (LADR-02).
- `snippet` options are fixed by the design; headline runs after `LIMIT` so cost tracks the 50/200 ceiling.
- Anchor `LIMIT` controls traverse input when the flag is set — 5 anchors means 5-anchor widening.
- A miss (empty retrieval result) is a recorded HLD-004 signal; a malformed refusal
  (400, parser never reaches retrieval) is not — refusals carry no feedback emission.

## Quality Constraints

Measurable NFRs live in [`./nfrs/`](./nfrs/). Constraints that change how code is written:

- Every predicate executes database-side — no post-materialisation filtering (NFR-02).
- `field:value` string must return the same rows as the equivalent structured request
  (row-identity equality, not SQL-text identity) (NFR-04).
- Branch plan tests must name the FTS GIN index with no `Seq Scan` per `tsquery`
  constructor; full-shape plan is captured informationally (NFR-02).

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-13 | HLD scaffolded | this HLD |
| 2026-10-13 | Drafted README (4 goals), LADRs 01–06, NFRs 01–04, C1 + flow + sequence diagrams, guardrails | keyword-search analysis |
| 2026-10-13 | Ralph loop 10/10 (syntax, injection/scope, rank/traverse, compat/NFR, shape, grammar, plan-honesty, disclosure, degenerate-input): bare=recency vs operators=rank, snippet additive-nullable, closed 12-code refusal enum, field-only valid, value/duplicate rules, branch-only GIN claim, one-round-trip traverse with disclosure + timeout rule, no-default-depth | keyword-search analysis |
| 2026-10-13 | Ralph loop 2 10/10 (grammar, authz-oracles, rank-pinning, traverse-atomicity, wire-compat, NFR-rigor, cross-HLD-drift, PG-semantics, DoS-caps, layering): stale Goal-3 para deleted, 14-code enum (+depth/complexity), phrase-via-composition, hyphen/colon arms picked, rank weights/normalization/max-side pinned, snippet wire rule + dossier/export exclusion, traverse txn/scope-every-hop/timeout-mapping, refusal-is-not-miss, shape-mapping to HLD-004 | this HLD |
| 2026-10-14 | Validity loop 3/3 (live-PG claims, internal consistency, cross-refs): prefix-stopword collapse corrected, `!!foo`→`!foo`, weights/norm pinned (uniform D, defaults, norm 0), version index named, EXPLAIN txn precondition, READ COMMITTED, explicit-AND + implicit-AND-bare + field-only-recency pinned, snippet casing unified, LADR-05 renamed to round-trip, deadline authority moved to odin LADR-006, what-is-special #1 added, demo phrase de-stopworded | this HLD |
