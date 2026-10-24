# AGENTS.md - .agents/skills

## TL;DR

Read [`README.md`](./README.md) first — skill index, switches, effort levels and frontmatter shape live there.

## Non-Negotiables

- **The installer-owned skills are copied verbatim into other repositories.** In `mimisbrunnr-*`,
  `ai-understanding`, `ai-brain-dump` and `_mimisbrunnr-shared`, a link may not leave `.agents/skills/` and
  a rule is not cited by a repo-relative path; `scripts/check_vendored_skills.py` fails the PR gate on both.
- **A message a script loads lives in that skill's own `assets/`, never in a shared folder.** `assets/`
  travels with the skill into a zip — the installer prunes only `tests/` and `__pycache__/` — so a
  skill-owned script may read it, while `_mimisbrunnr-shared/` owns code and a shared script never
  resolves a caller's assets (it takes an explicit path). Format is `.md` for prose and `.json` for
  structured data, never `.txt`. A skill resolves `<skill>/assets` from its own `scripts/`
  (`parents[1] / "assets"`), loads by file name, fills `{{placeholder}}` with plain `.replace` (never
  `string.Template`, whose `$` collides with prose), and fails closed on a missing, unreadable, empty
  or half-filled file. Only a message that is at least three lines or used at two call sites moves out
  of code; a one-line aside stays inline.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-24 | **The loaded-strings convention: per-skill `assets/`, no shared assets.** `mimisbrunnr-kvasir-understanding` moves its store-answer refusals, deprecated spellings and export preview/receipt prose into `assets/*.md`, loaded by one fail-closed helper; every moved message is byte-pinned. The threshold is stated with the rule — three lines or two call sites — and `mimisbrunnr-odin-context-memory` was checked and gained no folder, because its own scripts raise one-line messages once each. | this PR |
| 2026-10-19 | `ai-review` removed; `README.md` index, effort, naming and parent-ownership text updated | #295 |
| 2026-10-18 | **`README.md` index, effort table, naming table and switch sections no longer list the skills #291 removed** (`agile-github-*`, `ai-terse`, `ai-template-sync`, `context-load-*`, `create-hld`, `git-*`, `manage-rule-system`), and the parent-ownership paragraph names only the parents that still own a skill here. The same removal reached `.agents/AI_DEVELOPMENT_AGENTS.md`'s folder table and `.agents/settings.json`'s `Skill(...)` allow-list. | — |
| 2026-10-16 | **`ai-memory-context` added** — a local-first memory store. Odin's redact→dedupe→atomicity pipeline writing to `.context/context-memories/`, with no server, no network and no sibling script folder, so the whole skill ships as a single zip. Owned by this repo (not a template parent), installer-distributed with **no marketplace plugin entry** — following `ai-brain-dump`, the existing precedent for an owned skill that is not plugin-distributed. Three scripts are WET-vendored from `_mimisbrunnr-shared/` and held byte-equal by `tests/parity_shared_scripts.py`, which also fails if a vendored copy gains a `parents[2]` or `sys.path` insert. | — |
| 2026-06-21 | Initial version | #52 |
| 2026-07-31 | SARIF now omits baselined findings instead of marking them `suppressions` | — |
| 2026-09-14 | This-repo skills renamed | #55 |
| 2026-09-27 | `mimisbrunnr-ymir-bootstrap` added at `effort: xhigh` in `README.md` → Effort | — |
| 2026-09-27 | Every `SKILL.md` now carries YAML frontmatter | — |
| 2026-09-27 | Per-provider model selection removed from every skill | — |
| 2026-09-27 | Aligned with upstream `smooth-ai-report-review` PR #167 (master of the shared skills) | #167 |
| 2026-09-27 | Aligned with `smooth-devex-template` PR #84 (parent of most skills here) | #84 |
| 2026-10-02 | The parent-ownership paragraph in `README.md` no longer lists `ai-understanding` among the skills "kept byte-identical"… | #161 |
| 2026-10-14 | `_mimisbrunnr-shared/` created (HLD-010 B2): One shared script folder for every script used by more… | #222 |
| 2026-10-15 | Installer-owned skills are written to be vendored (HLD-010 NFR-05) | — |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
