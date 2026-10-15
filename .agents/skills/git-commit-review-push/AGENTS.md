# AGENTS.md - git-commit-review-push

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — trigger placement, the `%B` amend, branch rename and the pre-push checks live there.

## Non-Negotiables

- **This SKILL.md mirrors `smooth-ai-report-review` and stays byte-identical to it.** A local edit
  diverges and is lost on the next sync: change it upstream, then re-copy.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-07-07 | Folded the merge-commit guard into the step-4 code block (prose-only before), normalized its indentation… | #64 |
| 2026-07-07 | Initial AGENTS.md for the `git-commit-review-push` skill | — |
| 2026-08-03 | Matched trigger verification to the gate's whole-message matcher (`%B` + unanchored grep) and preserved… | #103 |
| 2026-08-04 | Closed the clean-tree gap | — |
| 2026-09-27 | Replaced the per-tool `models` frontmatter block with `effort: low` and dropped `model:` from `agents/openai.yaml` | #122 |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
