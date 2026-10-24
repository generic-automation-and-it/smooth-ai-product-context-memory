# AGENTS.md - .agents/skills

## TL;DR

Read [`README.md`](./README.md) first — skill index, switches, effort levels and frontmatter shape live there.

## Non-Negotiables

- **The installer-owned skills are copied verbatim into other repositories.** In `mimisbrunnr-*`,
  `ai-understanding`, `ai-brain-dump` and `_mimisbrunnr-shared`, a link may not leave `.agents/skills/` and
  a rule is not cited by a repo-relative path; `scripts/check_vendored_skills.py` fails the PR gate on both.
- **A message a script loads has exactly two allowed homes, and each script reads only its own.**
  A **skill-owned** script loads from that skill's `assets/`; a **shared** script loads from
  `_mimisbrunnr-shared/scripts/assets/` — the shared folder's own prose, resolved beside the script
  (`SCRIPTS / "assets"`), so a skill never reads the shared folder's assets and a shared script never
  reads a caller's. `assets/` travels with the skill into a zip — the installer prunes only `tests/` and
  `__pycache__/` — and the shared folder is refreshed whole, so both survive an install. A standalone
  plugin that keeps a WET copy of a shared script (`ai-understanding`) also keeps byte-identical copies
  of its assets, held by that skill's parity test. Format is `.md` for prose and `.json` for structured
  data, never `.txt`. A skill resolves `<skill>/assets` from its own `scripts/`
  (`parents[1] / "assets"`), loads by file name, fills `{{placeholder}}` by single-pass substitution
  (never `string.Template`, whose `$` collides with prose; a value is inserted verbatim and never
  re-scanned, so a value containing `{{…}}` prints literally), and fails closed on a missing,
  unreadable, empty or half-filled file. Only a message that is at least three **printed** lines or
  used at two call sites moves out of code; a one-line aside stays inline (measure printed lines, not
  source lines: wrapped source would qualify half the file). A cohesive family may move as a whole even
  when each member is one line — the decision-gate notes do — when the caller reads them together.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-24 | **The loaded-strings rule gained its second allowed home.** A shared script loads its prose from `_mimisbrunnr-shared/scripts/assets/`; a skill loads only its own `assets/`; a standalone plugin that copies a shared script copies its assets byte-identically and pins both the file set and the bytes. A cohesive family may move whole even when every member is one line — the decision-gate notes did | this PR |
| 2026-10-24 | **The loaded-strings convention: per-skill `assets/`, no shared assets.** `mimisbrunnr-kvasir-understanding` moves its qualifying user-facing prose into `assets/*.md` — messages of ≥3 **printed** lines, one-liners reused at ≥2 call sites, and the 10-line dump template (R4 names templates) — loaded by one fail-closed single-pass helper, with the template behind a function so a missing asset refuses from `main` instead of raising at import. Re-review moved twelve one-line one-offs back inline and added five missed qualifiers. Every asset byte-pinned. Measured in printed lines, not wrapped source lines. `mimisbrunnr-odin-context-memory` was checked and holds no `assets/` by the same bar — allowed, not excluded. | this PR |
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
