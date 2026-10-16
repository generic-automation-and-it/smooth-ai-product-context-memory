# AGENTS.md - create-worktask

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — the worktask contract, workflow and quality bar live there.

## Non-Negotiables

- **The Execution Profile is one contract in three files.** `SKILL.md` (Output structure, workflow
  step 4), `assets/WORKTASK.template.md` and `.agents/templates/AI_WORKTASK_PROMOTE_STANDALONE_TEMPLATE.md`
  change field-for-field together. `Commits during execution: not allowed` is the default that bites:
  under `defaultMode: bypassPermissions` nothing else stops an autonomous commit.
- **`assets/` stays free of HTML comments.** SkillSpector's static scan rates them `P2` "Hidden
  Instructions" (`create-hld/assets/*` carries baselined entries for exactly this); adding one needs a
  justified `.github/skillspector-baseline.yml` entry in the same PR. The scan covers `AGENTS.md` too —
  moving flagged text here does not hide it.
- **The prompt router lives outside the skill and only routes.** `.agents/hooks/worktask-skill-trigger.sh`
  hard-codes this skill's `SKILL.md` path — a rename or move updates it. Never put authoring workflow in
  the hook.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-09-20 | Initial; replaces the Claude-only `worktask-create.sh` hook | #74 |
| 2026-09-21 | P2 hidden-instructions rationale moved here from SKILL.md; `AGENTS.md` is scanned too | #71 |
| 2026-09-23 | Static-gate findings cleared: EA2 reworded; P6/MP2 baselined | #82 |
| 2026-09-27 | Execution Profile `Recommended session model` → `Recommended session effort` across the three files | #84 |
| 2026-09-27 | Shared Claude Code/Codex prompt router restored | #87 |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
