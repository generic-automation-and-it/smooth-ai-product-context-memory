# AGENTS.md - ai-review

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — modes, source routing, result routing and guardrails live there.

## Non-Negotiables

- **This directory mirrors `smooth-ai-report-review`; its SKILL.md stays byte-identical, including the
  `switches:` list.** A local fix diverges and is lost on the next sync: file it upstream, then re-copy.
- **Only the consumer skill is installed.** The review generator (`ai-review-report`) runs remotely through
  the reusable workflow in `pipeline-code-review-report.yml`; do not copy its tree in to "complete" the install.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-20 | Vendored `/ai-review` consumer skill from smooth-ai-report-review | — |
| 2026-07-05 | Documented the companion self-fix workflow that consumes low/medium review findings and posts an auto-fix… | — |
| 2026-09-19 | The PR template finally provides the section this skill writes into | #82 |
| 2026-09-19 | Re-synced `SKILL.md` and `scripts/copilot-review.sh` to upstream `main`, which had drifted ahead of this copy | — |
| 2026-09-27 | `models:` replaced by `effort: medium` | #122 |
| 2026-09-27 | That re-sync prohibition was promoted out of this changelog into Non-Negotiables, so the "re-copy"… | #122 |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
