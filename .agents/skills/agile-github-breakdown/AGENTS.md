# AGENTS.md - agile-github-breakdown

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — workflow, write gates, script contracts and exit codes live there.

## Architecture Decisions

### LADR-001 — Dry-run default; GitHub links fail soft

- **Status:** Accepted · **Date:** 2026-09-13
- **Decision:** No write without `--apply`. `link_sub_issue()` and `add_blocked_by()` return `False` and
  print a manual-link note instead of raising — some tokens cannot use those APIs.
- **Consequence:** Never make them hard-fail: an abort mid-run leaves created Tasks with no Feature
  parent and no report of what was created.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-09-13 | Ported from `epic-breakdown` onto vanilla GitHub Projects (Feature/Task/Project | #28 |
| 2026-09-13 | Copilot review | #28 |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
