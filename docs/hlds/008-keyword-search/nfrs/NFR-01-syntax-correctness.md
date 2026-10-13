# NFR-01: Syntax correctness

**Status:** Draft

## Requirement

The operator suite parses deterministically: a committed 40-case fixture (bare words,
implicit-AND, explicit `OR`, `-`/`NOT`, `( )` grouping, `"phrase"`, `prefix*`, mixed
combinations, plus 12 malformed inputs) passes at 1.0 — every well-formed case yields the
specified `tsquery` shape, every malformed case is refused with a stable reason code from
the closed set: `E_EMPTY_QUERY`, `E_EMPTY_GROUP`, `E_DANGLING_OPERATOR`,
`E_UNBALANCED_PAREN`, `E_UNTERMINATED_QUOTE`, `E_LONE_PREFIX`, `E_UNKNOWN_FIELD`,
`E_EMPTY_VALUE`, `E_DUPLICATE_FIELD`, `E_INVALID_FIELD_VALUE`,
`E_EMPTY_AFTER_NORMALIZATION`, `E_NO_POSITIVE_TERM`.

## Verification

L0 parser tests (no database) assert the operator-tree output per case, and L1 search
tests assert the end-to-end set semantics (phrase narrows vs. AND, `OR` widens, `-`
excludes) against a seeded corpus. Mutation check: flipping any operator mapping
(`|`↔`&`, `!` dropped, phrase→plain) fails the suite.

## Acceptance Criteria

- [ ] 40/40 fixture cases green; 12/12 malformed inputs refused with the closed reason
  enum (unbalanced quote/paren, empty query/group, dangling operator, lone prefix,
  unknown field, empty value, duplicate field, invalid field value, stopword-only
  collapse, NOT-only without field predicate).
- [ ] Same string always yields the same `tsquery` (determinism asserted by repeated run).
- [ ] No well-formed query returns the unfiltered corpus (silent-widening guard).

## Applies To

Goals 1–2; LADR-01, LADR-02, LADR-03.
