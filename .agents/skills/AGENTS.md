# AGENTS.md - .agents/skills

## TL;DR

Read [`README.md`](./README.md) first — skill index, switches, effort levels and frontmatter shape live there.

## Non-Negotiables

- **The installer-owned skills are copied verbatim into other repositories.** In `mimisbrunnr-*`,
  `ai-understanding`, `ai-brain-dump` and `_mimisbrunnr-shared`, a link may not leave `.agents/skills/` and
  a rule is not cited by a repo-relative path; `scripts/check_vendored_skills.py` fails the PR gate on both.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
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
