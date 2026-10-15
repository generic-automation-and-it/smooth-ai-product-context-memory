# AGENTS.md - git-commit

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — logical-unit grouping, message rules, fallback types and `--autonomous` live there.

## Non-Negotiables

- **Every new ask-the-user branch needs a `--autonomous` bypass.** The flag arrives forwarded from
  `git-commit-push-pr` and `git-commit-push` and promises no questions across the whole chain; an
  unbypassed ask here or in `git-commit-push-pr` stalls an autonomous run.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-12 | Initial version | — |
| 2026-06-12 | Type list restored as explicit "Fallback types" (synced to git-policy, `style` dropped) for runners… | — |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
