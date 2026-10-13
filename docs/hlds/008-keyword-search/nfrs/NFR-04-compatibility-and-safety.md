# NFR-04: Compatibility and safety

**Status:** Draft

## Requirement

Bare-text queries branch to `plainto_tsquery` with recency order and no rank, returning
the identical set and order as today; operator queries branch to the `to_tsquery` family
with `ts_rank_cd` plus recency tiebreak. Unknown `field:` is refused, never ignored; no
memory content reaches any log, span, or recall-feedback payload on any path including
parse errors and headline generation. Refusal reasons are the closed NFR-01 enum and
carry the field *name* at most — never a field value, lexeme, phrase, or snippet.

## Verification

Back-compat L1 suite replays today's `FreeTextRecallTests` corpus through the new parser
with bare strings and asserts set+order equality. Refusal tests assert stable reason codes
for unknown fields and malformed input. Secret/content scan: log-capture test asserts no
statement, summary, snippet, or query lexeme beyond identifiers/counts appears in logs;
feedback-record inspection asserts identifiers only.

## Acceptance Criteria

- [ ] All existing `FreeTextRecallTests` pass unmodified against the new path; operator
  rank is asserted only on the new fixture, never on the bare corpus.
- [ ] Structured-request equivalence: `field:value` string ≡ matching `QueryMemories.Request`
  by row identity (set equality) on a seeded corpus — not SQL-text identity.
- [ ] Log/feedback capture contains zero content tokens on the error and headline paths;
  negative test echoes `tag:<lexeme>` and asserts the lexeme's absence from reasons,
  logs, and feedback.

## Applies To

Goals 1–4; LADR-01, LADR-03, LADR-04; HLD-001 confidentiality rule, HLD-004 feedback shape.
