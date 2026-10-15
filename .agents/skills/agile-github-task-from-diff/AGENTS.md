# AGENTS.md - agile-github-task-from-diff

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — flags, workflow, branch rename and gotchas live there.

## Non-Negotiables

- **Backend detection is hard-coded to this repository's layout.** The `src/`/`SmoothAiProductContextMemory`
  prefix check appears twice in the script — `classify_horizontal_slice()` and the acceptance-criteria
  checklist. A rename or ported layout updates both, or backend diffs lose the `backend` layer: no `feat`
  branch suggestion and no build-check criterion.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-12 | Initial version | — |
| 2026-09-23 | Quick wins from the `pbi-draft-jira-task` comparison | — |
| 2026-09-27 | Added `--noparentid` (repo-only task | #122 |
| 2026-09-27 | Re-synced this repo's copy wholesale to `smooth-devex-template` `main` (PR #84 and the 2026-09-23 row… | #84 |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
