# AGENTS.md - git-commit-push

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — delegation to `git-commit`, branch-rename derivation and arguments live there.

## Non-Negotiables

- **`scripts/push.sh` exits 0 when there is nothing to push.** `git-commit-push-pr` runs this skill
  first even when it only rewrites an existing PR's description; a non-zero "nothing to push" exit
  breaks that update path.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-12 | Initial version | — |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
