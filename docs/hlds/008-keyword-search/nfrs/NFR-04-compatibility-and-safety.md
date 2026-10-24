# NFR-04: Compatibility and safety

**Status:** Draft

## Requirement

Bare-text queries branch to `plainto_tsquery` with recency order and no rank, returning
the identical set and order as today; operator queries branch to the `to_tsquery` family
with `ts_rank_cd` plus recency tiebreak. Unknown `field:` is refused, never ignored; no
memory content reaches any log, span, or recall-feedback payload on any path including
parse errors and headline generation. The global exception mapper strips PG
`Message`/`Detail`/`Hint`/`Where` on all paths — a `PostgresException` with SqlState
`57014` (query_canceled embeds query text) maps to `stoppedEarly` + stable code +
correlation-id only, never PG text. `EnableSensitiveDataLogging=false` and
`IncludeErrorDetails=false` hold in all envs with no env-conditional override, so bound
`tsquery` text never lands in logs (startup assertion fails the build if either flips).
Refusal reasons are the closed NFR-01 enum and carry the field *name* at most — never
a field value, lexeme, phrase, or snippet.

## Verification

Back-compat: `FreeTextRecallTests` pass unmodified (adapter allowed only outside
assertions — parser entry, not expected values); bare-vs-structured equivalence in
`KeywordCompatTests` on the committed 6-memory corpus. Safety: `KeywordRefusalTests`
(stable codes) and `KeywordRedactionTests` (log/feedback capture over Serilog + OTLP
spans + feedback records; content token = any query lexeme, phrase, field value, or
snippet substring, modulo stemming). Mapper coverage: `KeywordRedactionTests` throws
`PostgresException(57014)` with lexemes in `Message`+`Detail` and asserts the HTTP body
plus Serilog/OTLP capture contain zero content tokens; a `DbContextOptions` guard test
fails the build if sensitive logging or error details turn on; a grep guard rejects
`EnableSensitiveDataLogging(true)`.

## Acceptance Criteria

- [ ] All existing `FreeTextRecallTests` pass unmodified against the new path; operator
  rank is asserted only on the new fixture, never on the bare corpus.
- [ ] Structured-request equivalence: `field:value` string ≡ matching `QueryMemories.Request`
  by row identity (set equality) on a seeded corpus — not SQL-text identity.
- [ ] Log/feedback capture contains zero content tokens on the error and headline paths;
  negative test echoes `tag:<lexeme>` and asserts the lexeme's absence from reasons,
  logs, and feedback.
- [ ] Per-path emission: anchor-hit + traverse-incomplete (cancel, post-anchor error,
  deadline) emits hit records under the anchor's shape with `passesIncomplete`
  carrying incompleteness — never a miss, never a second row; anchor-empty emits one
  miss; `E_*` refusals emit nothing. Shape values stay inside the existing HLD-004
  five (`free_text`, `facet_only`, `ticket_scoped`, `group_scoped`, `unfiltered`) —
  operator → `free_text`, field-only → `facet_only`/scoped, traverse → anchor's shape —
  with a negative test for CHECK `23514`; a new shape needs an HLD-004 amendment first.

## Applies To

Goals 1–4; LADR-01, LADR-03, LADR-04; HLD-001 confidentiality rule, HLD-004 feedback shape.
