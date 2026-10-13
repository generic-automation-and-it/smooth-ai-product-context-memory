# Keyword Search — High-Level Design

| | |
|---|---|
| **Status** | In Discovery |
| **Owner** | generik0 |
| **Tracker** | TBD (owner: generik0 — file issue before Prototype) |
| **Last updated** | 2026-10-13 |

> Discovery / prototyping HLD. This document delivers **intent + spec** — what we are
> building and why, the decisions behind it, and the quality bar it must meet. It does
> **not** contain an implementation plan; execution (phasing, sub-issues, sequencing) is
> tracked in the issue/work tracker.

## Intent

Recall today is lexeme-AND only: `NpgsqlMemorySearch` matches `plainto_tsquery('english', text)`
against `name || description` OR `statement || content_summary`, ordered by recency
(`ValidFrom desc`). There is no phrase, no `OR`/`NOT`, no prefix, no field scoping in one
string, no relevance rank, no snippet. Agents compensate client-side (`deepsearch.py` fans
out to ten calls); humans get no Obsidian-like surface at all.

This design adds **Obsidian-like keyword search in one query string** on top of the shipped
`english` FTS pair: phrase, `AND`/`OR`/`NOT` with grouping, prefix `*`, and `field:value`
sugar that maps onto the existing structured predicates. Ranking becomes relevance-first
(`ts_rank_cd`) with `ts_headline` snippets. No new store, no schema change, no vectors,
no server-side model — the `pg_trgm` rejection and every scope/status/temporal semantic
stay intact.

## Key Goals

### 1. One string expresses what today takes ten calls

An agent or operator writes `migration OR rollback -proposed "down step" tag:schema`
instead of issuing a baseline plus four keyword passes and filtering in memory. The string
supports implicit-AND (`a b`), explicit `OR`, `-`/`NOT` exclusion, `( )` grouping, `"phrase"`,
and trailing-`*` prefix. Bare words keep today's `plainto_tsquery` behaviour so existing
callers see no regression.

**Acceptance criteria / DoD**

- The documented operator set (`AND`/implicit, `OR`, `-`/`NOT`, `( )`, `"phrase"`, `prefix*`)
  parses to a single database-executed `tsquery`; bare text behaves as today.
- Malformed input (unbalanced quote/paren, empty query, `*` alone) is a validation refusal
  with a stable reason, never a silent widening to all rows.
- The operator suite passes a committed 40-case parse+recall fixture (bare, phrase, OR,
  NOT, grouping, prefix, mixed) at 1.0.

### 2. Field scoping reuses existing predicates, adds none

`tag:`, `facet:`, `kind:`, `repo:`, `initiative:`, `status:`, `group:` in the string map onto
the `MemorySearchCriteria` fields that already exist — conjunctively with the FTS match and
with the scope plan, lifecycle, and temporal filters. No new column, no new index, no new
filter semantic. Unknown fields are refused, not ignored.

**Acceptance criteria / DoD**

- Every documented `field:value` maps 1:1 to an existing criteria field; the generated SQL
  predicate is identical to the equivalent structured request.
- Field scoping composes with scope-plan, `ExcludeProposed`, `CurrentOnly`, and `AsOf`
  without bypass (the lifecycle negative controls stay green).
- Unknown field, empty value, or conflicting duplicate field is a validation refusal.

### 3. Relevance outranks recency on operator queries; bare keeps recency

Operator queries (any explicit `OR`, `-`/`NOT`, `( )`, `"phrase"`, or `prefix*`) order by
`ts_rank_cd` first, recency (`ValidFrom desc, uuid, version`) as the deterministic
tiebreak. Bare-text queries keep today's order (`ValidFrom desc, uuid, version`) with no
rank — back-compat is absolute for the shipped path. Each hit carries a nullable
`Snippet` (`ts_headline` over the matched side, subject vs. claim) so the skill's semantic
judgement — which stays client-side — has evidence beyond the full cheap row. Rank input
is the same `tsquery` from Goal 1; no separate ranking signal. Existing response fields
are unchanged; `snippet` (camelCase, always present, nullable) is additive and null when
the headline has nothing to mark. Rank uses default weights (index vectors are
weightless — no `setweight`) and default normalization; both-match (subject + claim)
takes the max side; endpoints carry no snippet. Bare, field-only, and field+bare
(zero explicit operators) order by recency; any explicit operator branches to rank.
Dossier-bundle and forensic-export projections exclude `snippet` by name, as do all
compat/equivalence comparisons and the `deepsearch` dedupe key (uuid/version only).

**Acceptance criteria / DoD**

- On a seeded rank fixture of operator queries (multi-lexeme vs. single-lexeme vs.
  stem-only candidates) the multi-lexeme exact row orders first regardless of `ValidFrom`;
  bare-text order is unchanged from today.
- Snippet marks the matched lexemes on the matched side, is nullable, and never includes blob bodies.
- Rank changes ordering only; the result *set* for a fixed query is unchanged by ranking.

### 4. Keyword anchors compose with graph traversal server-side

Today `deepsearch.py` chains baseline + keyword + depth-one traversals client-side (up to
ten calls, 400-cap dedupe). This design lets a single request ask for keyword anchors plus
bounded AGE widening (depth 1–5, the deliberate HLD-003 ceiling) in one HTTP round trip
(handler transaction, scope gate on every hop exactly as HLD-003), so the traversal
starts from the *ranked anchor set* rather than a client-truncated list. Client-side
`deepsearch` keeps working unchanged; the server composition is the deliberate path for
new callers. `depth` is required explicitly (no default); the composed call runs under a
Prototype-measured `SET LOCAL statement_timeout` inside the recall-deadline budget
(HLD-004 bounds the whole call, timeout is defence-in-depth per HLD-003 — 100 ms is not
reused as this shape's target).

**Acceptance criteria / DoD**

- A keyword+traverse request returns post-scope, post-`LIMIT` anchor survivors plus
  widened endpoints in one HTTP round trip with disclosed counts (`anchorsEligible` null
  when the anchor phase never answers, `stoppedEarly` vs. `budgetExhausted`,
  `passesIncomplete`, distinct `endpointsOutsideSelector`) as a documented `deepsearch`
  disclosure subset (see LADR-05 §Disclosure).
- Traversal honours the same scope plan and group selector as the anchor query; endpoints
  outside the selector are dropped and counted, never leaked.
- Without the traverse flag the response is wire-compatible with today's query:
  existing fields unchanged, plus nullable `Snippet`; no traverse fields unless set.

## Core Separation of Concerns

> The string is syntax; the criteria are semantics; the database executes both.

Parsing (string → operator tree → criteria + provider-neutral query model) is pure and
lives in Application; only Infrastructure serialises the model to `tsquery` constructors
and executes it. The handler resolves identity and scope, then runs parse → translate;
`group:` disagreements (string vs. param vs. ticket group) intersect conjunctively to an
empty set — never an override. Nothing caller-supplied reaches statement text; the built
query travels as a bound parameter *and* lexemes are quoted per `tsquery` quoting rules
(binding alone does not stop operator injection).

This split is what keeps the `english` index pair servable, the `pg_trgm` rejection
untouched, and every predicate database-executed — the three properties HLD-001 and
HLD-003 exist to protect.

## Guiding Principle — Widen candidates, never authority

> Stemming and syntax widen *which* candidates return; nothing here judges, scopes, or keeps.

- Parsing stays total and deterministic: same string, same `tsquery`, same set — the skill
  judges relevance, the API only retrieves.
- We will deliberately **not** add vectors, server-side semantic judgement, fuzzy
  (`pg_trgm`) matching, auto-capture, or in-place correction. Each is refused by a
  recorded decision (`what-is-special` #2/#3/#5).
- A miss stays a signal (HLD-004 recall-feedback), not an error to hide with threshold
  magic.

---

## Diagrams

- [System Context (C1), query flow, recall sequence](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADRs 01–04 are strategic (*what* and *why*); LADR-05–06 are tactical (*how*). Each is a
single decision — a horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-query-string-parses-in-application-translates-in-infrastructure.md) | Query string parses in Application, translates in Infrastructure | Draft |
| [LADR-02](./ladrs/LADR-02-english-pair-preserved-operators-use-totsquery.md) | `english` index/query pair preserved; operators use `to_tsquery` family | Draft |
| [LADR-03](./ladrs/LADR-03-field-sugar-maps-onto-existing-criteria-conjunctively.md) | `field:value` sugar maps onto existing criteria, conjunctively | Draft |
| [LADR-04](./ladrs/LADR-04-rank-cd-orders-headline-shows-evidence.md) | `ts_rank_cd` orders, `ts_headline` shows evidence | Draft |
| [LADR-05](./ladrs/LADR-05-keyword-plus-traverse-composes-in-one-statement.md) | Keyword anchors + bounded traverse compose in one round trip | Draft |
| [LADR-06](./ladrs/LADR-06-fuzzy-stays-rejected-prefix-covers-typeahead.md) | Fuzzy stays rejected; prefix covers typeahead | Draft |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable
target, a verification mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-syntax-correctness.md) | Correctness | 40-case operator fixture at 1.0; malformed input refused | Draft |
| [NFR-02](./nfrs/NFR-02-index-served-performance.md) | Performance | Branch GIN-served; p95 ≤ 2x re-measured bare baseline | Draft |
| [NFR-03](./nfrs/NFR-03-recall-precision.md) | Retrieval quality | Recall ≥ 0.78, precision ≥ 0.85 on extended fixture | Draft |
| [NFR-04](./nfrs/NFR-04-compatibility-and-safety.md) | Compatibility + safety | Bare queries unchanged; no content in logs; unknown field refused | Draft |
