# NFR-01: Syntax correctness

**Status:** Draft

## Requirement

The operator suite parses deterministically: a committed 40-case fixture (bare words,
implicit-AND, explicit `OR`, `-`/`NOT`, `( )` grouping, `"phrase"`, `prefix*`, mixed
combinations, plus 14 malformed inputs) passes at 1.0 — every well-formed case yields the
specified operator-tree shape, every malformed case is refused with a stable reason code from
the closed set: `E_EMPTY_QUERY`, `E_EMPTY_GROUP`, `E_DANGLING_OPERATOR`,
`E_UNBALANCED_PAREN`, `E_UNTERMINATED_QUOTE`, `E_LONE_PREFIX`, `E_UNKNOWN_FIELD`,
`E_EMPTY_VALUE`, `E_DUPLICATE_FIELD`, `E_INVALID_FIELD_VALUE`,
`E_EMPTY_AFTER_NORMALIZATION`, `E_NO_POSITIVE_TERM`, `E_INVALID_DEPTH`,
`E_QUERY_TOO_COMPLEX`.

## Verification

`KeywordParserTests` (L0, no database) assert the operator-tree output per case
(`docs/hlds/008-keyword-search/nfrs/NFR-01-fixture.md`, Prototype deliverable: 40 well-formed =
8 bare/implicit-AND, 6 OR, 6 NOT/exclusion, 6 grouping, 6 phrase, 4 prefix, 4 mixed —
plus 14 malformed, one per refusal code); `KeywordSearchTests` (L1) assert the
end-to-end set semantics against the seeded corpus. Tree determinism is asserted by 10x
repeated runs; `tsquery`-text assertions live in the Infrastructure fixture only
(LADR-01 layering). Mutation check (`KeywordParserMutation`, Prototype tooling):
flipping any operator mapping (`|`↔`&`, `!` dropped, phrase→plain, `:*` dropped) fails
the suite.

## Acceptance Criteria

- [ ] 40/40 fixture cases green; 14/14 malformed inputs refused with the closed reason
  enum (unbalanced quote/paren, empty query/group, dangling operator, lone prefix,
  unknown field, empty value, duplicate field, invalid field value, invalid depth,
  over-complexity cap, stopword-only collapse, NOT-only without field predicate).
- [ ] Same string always yields the same `tsquery` (determinism asserted by repeated run).
- [ ] No well-formed query returns the unfiltered corpus (silent-widening guard).

## Applies To

Goals 1–2; LADR-01, LADR-02, LADR-03.
