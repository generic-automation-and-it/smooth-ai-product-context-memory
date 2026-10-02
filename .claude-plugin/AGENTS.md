# AGENTS.md - Claude Plugin Marketplace

## TL;DR

`marketplace.json` declares what this repo distributes through Claude Code's plugin marketplace. Nothing in the repo consumes it; no test asserts it.

## Non-Negotiables

- Every name in a `skills` array MUST be a skill folder that exists under `.claude/skills/` (mirrored from `.agents/skills/`). Never list a skill that is not on disk.
- `ai-understanding` is owned by upstream `smooth-devex-template` and kept byte-identical to it. The marketplace distributes it; it does not own it. Never edit the skill to match the manifest — edit the manifest to match the skill.
- The `mimisbrunnr` plugin owns only the `mimisbrunnr-*` skills. A non-prefixed skill gets its own plugin entry.

## Key Behaviors

- Marketplace `mimisbrunnr`, two plugins: `mimisbrunnr` (the seven `mimisbrunnr-*` skills: odin-context-memory, vitsmunir-dump, muninn-recall-feedback, understanding, saga-dossier, ymir-bootstrap, heimdallr-find-session-metadata) and `ai-understanding` (one skill).
- There is no `plugins/` tree and no `plugin.json`; skill sources of truth are `.agents/skills/<name>/SKILL.md`, mirrored at `.claude/skills/<name>/SKILL.md`. Each plugin entry's `source` points at this repository itself.
- When adding a skill: add the folder under `.agents/skills/` per `.agents/skills/AGENTS.md` (folder name equals `name:` frontmatter), mirror it under `.claude/skills/`, then append the name to the owning plugin's `skills` array here and update that plugin's `description`.
- Validate with `python3 -c "import json; json.load(open('.claude-plugin/marketplace.json'))"` — there is no schema test beyond well-formedness.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-02 | Expanded the `mimisbrunnr` plugin from 4 to all 7 `mimisbrunnr-*` skills and added an `ai-understanding` plugin entry; created this file so the manifest has local owner context. | issue 164, PR 165 |
