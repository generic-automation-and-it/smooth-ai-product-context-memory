# AGENTS.md - manage-rule-system

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — rule directory, frontmatter contract and tool matrix live there.

## Non-Negotiables

- **The convention has two homes.** Frontmatter template, scoping table and tool matrix are in both SKILL.md
  and `.agents/rules/meta/rules.instructions.md`; a contract change edits both.
- **SKILL.md has two injectors with separate trackers.** `scripts/inject-context.sh` (UserPromptSubmit) and
  the `load-agents-context` PostToolUse hook dedupe independently, so one session can receive it twice.
  Repeat injection on one path means a broken session-id chain — fix that, not the hook regex.
- **Downstream sync never deletes.** `ai-template-sync --rules-only` UPSERTs `.github/instructions/`, so a
  renamed or removed rule stays loaded in consumer repos beside its replacement.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-12 | Initial | #40 |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
