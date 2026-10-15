# AGENTS.md - git-sync

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — the `safe-sync.sh` sentinel contract, both modes and the resolution strategy live there.

## Non-Negotiables

- **Never add `git merge --abort` to `scripts/safe-sync.sh`.** Conflicts stay unmerged in the
  working tree on purpose: Mode 2 (`--fix`) resolves from that state, and Mode 1 hands the same
  state to the user. An abort on conflict silently disables `--fix`.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-12 | Initial version | — |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
