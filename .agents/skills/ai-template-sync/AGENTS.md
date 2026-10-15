# AGENTS.md - ai-template-sync

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — phases, flags, Sections A–E and guardrails live there.

## Non-Negotiables

- **`sync.sh` and `sync.ps1` change together.** Windows consumers get only `.ps1`; a flag or section
  added to one lands in the other in the same change. Match behaviour, not syntax: `.ps1` ends
  rules-only with `return` inside `try` so `finally` removes the temp clone (`.sh` uses `exit 0` plus an
  `EXIT` trap).
- **Section D under `--overwrite global` is the one place UPSERT breaks.** Both scripts delete
  `.github/instructions` before copying the template's, dropping landing-only rules — including rules
  SKILL.md Phase 2 just moved there. `--rules-only` merges instead (no deletes). Don't copy the
  Section D pattern into another section.

## Architecture Decisions

### LADR-001 — `--rules-only` is the cross-repo rules channel

- **Status:** Accepted · **Date:** 2026-06-12
- **Decision:** distribute this repo's rule system through `sync.sh`/`sync.ps1 --rules-only`, pinned
  via `--template-ref`, not per-tool marketplace wrappers (Claude plugins, Copilot org instructions —
  each covers one tool).
- **Consequence:** a future wrapper shells out to `sync.sh --rules-only` rather than reimplementing the
  copy and the rules-layout refusal.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-12 | Initial version | — |
| 2026-09-27 | Both scripts refuse a credentialed `--template-url` | #128 |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
