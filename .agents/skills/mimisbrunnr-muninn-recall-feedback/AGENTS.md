# AGENTS.md - mimisbrunnr-muninn-recall-feedback

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — the three tuning queries, their arguments and the credential setup live there.

## Non-Negotiables

- **Env vars and who receives them.** `scripts/recall_feedback.sh` reads `CONTEXT_MEMORY_READ_TOKEN`
  (`never-recalled`, `miss-rate`), `CONTEXT_MEMORY_WRITE_TOKEN` (`reset`) and `CONTEXT_MEMORY_BASE_URL`.
  Only `recall_feedback_curl` reads a token, and it reaches curl's stdin alone: never argv, never a file.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-01 | An unparseable base URL is refused with fixed text | #148 |
| 2026-10-01 | Harness runs in `pr-gate.yml` on 3.9 and 3.12 | #148 |
| 2026-10-01 | Guard extracted to a script with a harness | #148 |
| 2026-10-02 | Renamed to `mimisbrunnr-muninn-recall-feedback` | #161 |
| 2026-10-05 | `;params` passed as a bare origin (`urlparse` moves them out of `path`) | #182 |
| 2026-10-05 | Queries take the operator's window | #179 |
| 2026-10-05 | Refusals echoed the base path (a pasted credential) | #179 |
| 2026-10-06 | Call sites pass a capability (`read`/`write`), never a token | #190 |
| 2026-10-06 | `urlparse` reads `host?` as an empty query, so bare `?`, `#`, `;` passed as… | #188 |
| 2026-10-06 | Header file replaced by stdin (`-H @-`) | #184 |
| 2026-10-07 | Token storage between shells is operator machine config (provisioned env file, managed `~/.zshrc` block) | — |
| 2026-10-07 | `SKILL.md` lists `::1` among accepted loopback hosts | #200 |
| 2026-10-07 | Dates checked against the calendar, not just the shape (`2030-02-30` refused) | — |
| 2026-10-07 | One executable script with subcommands replaces sourcing plus hand-built `recall_feedback_curl` lines | #196 |
| 2026-10-15 | The guard-order Non-Negotiable now describes the script (token read early, nothing sent before the… | — |
| 2026-10-15 | CI claim scoped to upstream's PR gate, with the manual command for a vendoring… | — |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
