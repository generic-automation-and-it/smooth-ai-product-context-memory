# AGENTS.md - Claude Plugin Marketplace

## TL;DR

`marketplace.json` declares what this repo distributes through Claude Code's plugin marketplace. Nothing in the repo consumes it; no test asserts it.

## Non-Negotiables

- Every name in a `skills` array MUST be a skill folder that exists under `.claude/skills/` (mirrored from `.agents/skills/`). Never list a skill that is not on disk.
- `ai-understanding` is upstream-owned from `smooth-devex-template`, but this repo carries accepted local divergences recorded in `.agents/skills/ai-understanding/AGENTS.md`'s changelog — the case-folded collision check, `context_root()` root derivation, the `--promote` evidence threshold, and the index generator's durability guard. `ai-template-sync` would revert them. The marketplace distributes it; it does not own it. Never edit the skill to match the manifest — edit the manifest to match the skill, and never revert a local fix to restore byte-identity with the template.
- The `mimisbrunnr` plugin owns only the `mimisbrunnr-*` skills. The marketplace distributes exactly one non-prefixed skill — `ai-understanding`, as its own plugin — so a non-prefixed name does not entitle a skill to a plugin entry; whether a new non-prefixed skill is distributed is the owner's decision.

## Key Behaviors

- Marketplace `mimisbrunnr`, two plugins: `mimisbrunnr` (the seven `mimisbrunnr-*` skills: odin-context-memory, vitsmunir-dump, muninn-recall-feedback, understanding, saga-dossier, ymir-bootstrap, heimdallr-find-session-metadata) and `ai-understanding` (one skill).
- There is no `plugins/` tree and no `plugin.json`; skill sources of truth are `.agents/skills/<name>/SKILL.md`, mirrored at `.claude/skills/<name>/SKILL.md`. Each plugin entry's `source` points at this repository itself.
- When adding a skill: add the folder under `.agents/skills/` per `.agents/skills/AGENTS.md` (folder name equals `name:` frontmatter), mirror it under `.claude/skills/`, then append the name to the owning plugin's `skills` array here and update that plugin's `description`.
- Skills in the `mimisbrunnr` plugin depend on each other **by sibling path**, so the plugin is only correct as a whole: `mimisbrunnr-heimdallr-find-session-metadata` screens ticket candidates with `mimisbrunnr-odin-context-memory/scripts/redact.py` (missing → it reports no tickets and `ticketsUnavailable`, fail closed), and `mimisbrunnr-kvasir-understanding` and `mimisbrunnr-saga-dossier` run the heimdallr reporter for autofill (missing → no autofill). Never split these into separate plugins or drop one from the `skills` array without moving that dependency.
- Validate with `python3 -c "import json; json.load(open('.claude-plugin/marketplace.json'))"` — there is no schema test beyond well-formedness.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-05 | Recorded the cross-skill sibling-path dependencies the `mimisbrunnr` plugin carries: heimdallr → odin's `redact.py` (new in issue 182; fail-closed `ticketsUnavailable` when missing) and kvasir/saga → heimdallr's reporter (autofill). The manifest does not change; the record exists so a plugin split or a `skills` array edit does not silently break the redactor lookup. | issue 182 |
| 2026-10-02 | The `ai-understanding` plugin `description` now separates the two pairs instead of collapsing them: it read "import, publish and consume them across workspaces", which told a marketplace reader that importing pulls knowledge from another workspace when `--import` is the load-it-back direction (session↔disk) and only publish/consume cross the workspace boundary. The preceding row claimed this edit was already justified by that same rule; the text it landed on did not separate the pairs any better than the text it replaced. Wording only. | PR review |
| 2026-10-02 | Two Non-Negotiables asserted more than the manifest does, and both were false as written. "Kept byte-identical to" upstream contradicted the divergence record in `.agents/skills/ai-understanding/AGENTS.md` (case-folded collision check, `context_root()`, `--promote` threshold, durability guard) — an agent reading that clause had no sanctioned way to fix a genuine skill defect and the natural reading ("never edit the skill") licensed reverting those fixes; the clause now states the divergences and the `ai-template-sync` consequence, and forbids restoring byte-identity. "A non-prefixed skill gets its own plugin entry" was broader than the manifest, which distributes exactly one non-prefixed skill out of the 15 present; it now names that boundary and leaves whether a new non-prefixed skill is distributed to the owner rather than asserting a taxonomy this repo never declared. The `ai-understanding` plugin `description` said "across sessions" where the skill's own contract separates the pairs (export/import are session↔disk, publish/consume are workspace↔workspace) and dropped the byte-identity claim. | issue 164, PR 165 |
| 2026-10-02 | Expanded the `mimisbrunnr` plugin from 4 to all 7 `mimisbrunnr-*` skills and added an `ai-understanding` plugin entry; created this file so the manifest has local owner context. | issue 164, PR 165 |
