# AGENTS.md - git-commit-push-pr

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — draft/ready, issue auto-close, title derivation, template filling and PR update live there.

## Non-Negotiables

- **`scripts/get-pr-metadata.sh` hard-codes the branch-type vocabulary in its regex.** A type added
  to or removed from git-policy updates that regex too (and `git-commit`'s fallback list), or
  branches of the new type return an empty `type` and are treated as non-conforming.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-12 | Initial version | — |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
