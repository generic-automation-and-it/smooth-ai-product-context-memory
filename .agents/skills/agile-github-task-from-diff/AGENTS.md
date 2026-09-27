# agile-github-task-from-diff — AGENTS.md

## TL;DR

Script-driven skill: `create_github_task_from_diff.py` owns diff classification, issue authoring, project add, sub-issue linking, and the branch-rename suggestion; the agent only decides the feature link and executes the suggested rename.

## Non-Negotiables

- **Don't author the issue title/body by hand** — the script is the source of truth for the `[layer]` title format and acceptance-criteria checklist. Hand-authored issues drift from the horizontal-slicing contract.
- **Never guess a branch `<type>` or slug** when the script's suggestion looks wrong — stop and ask, per the format-enforcement rule in `git-policy.instructions.md`.

## Key Behaviors

- `classify_horizontal_slice()` keys the `backend` layer off path prefixes `src/` and `SmoothAiProductContextMemory` — when this template repo is renamed, that prefix list must be updated or backend changes silently classify as `general`.
- The script's `LAYER_TO_TYPE` map drives the suggested branch name printed after creation; `git-commit-push-pr` later parses the issue number back out of that branch name for its `Closes #` link — the two skills are coupled through the `<type>/<issue>-slug` convention, not through any shared code.
- Sub-issue linking uses a REST endpoint (`POST .../sub_issues`) that fails soft: the script prints a manual-link fallback instead of erroring, so the Task is never lost. It sends the child's **database `id`** (resolved by a follow-up GET, posted with `-F`), not its number — so a "sub-issue link failed" warning now signals a real problem (token scope, cross-owner parent, API outage), and the script prints `gh`'s stderr beside it. It is no longer the expected outcome.
- The `--label` pre-check is a read-only GET: 404 → issue created unlabeled with a printed `gh label create` fix command, exit `0`; any other error → warn and apply the label anyway, so an auth/network hiccup never silently drops it. The script never creates the label.
- `--noparentid` makes a **repo-only** task: no Project add and no sub-issue link. It is rejected by `parse_args()` (argparse exit `2`) when combined with `--feature-issue`, because silently dropping a parent the user named would lose the link without trace. It differs from `--no-project`, which still links a given parent. With no parent, the first acceptance-criteria item scopes against the repo instead of a Feature.
- `allowed-tools` pre-approves only the script and read-only commands. `git branch -m` and `gh label create` are left out on purpose, so they still hit the permission prompt and run only after the user explicitly confirms.

## Test References

`scripts/test_create_github_task_from_diff.py` — stdlib `unittest`, patches the module's `run`/`run_json` so nothing shells out; `--noparentid` parsing is tested through `parse_args(argv)`. Run directly: `python3 .agents/skills/agile-github-task-from-diff/scripts/test_create_github_task_from_diff.py`. Deliberately not wired into CI, matching `agile-github-breakdown`.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-12 | Initial version. Branch-rename derivation moved from SKILL.md prose into the script (`suggest_branch_name`); stale `ProsmarBunkering` path prefix removed. | |
| 2026-09-23 | Quick wins from the `pbi-draft-jira-task` comparison: sub-issue link sends the database `id` with `-F` and prints `gh` stderr on failure (it never linked before); missing label no longer hard-fails create; negative triggers, `## Gotchas`, and a read-only `allowed-tools` added to SKILL.md; L0 tests added. `allowed-tools` deliberately omits `gh label create`/`git branch -m` — listing them would pre-approve writes the skill says need confirmation. | |
| 2026-09-27 | Added `--noparentid` (repo-only task: no Project, no parent; mutually exclusive with `--feature-issue`). Argument parsing extracted into `build_parser()`/`parse_args(argv)` so the flag conflict is unit-testable. Frontmatter normalised to the shared shape; `models` replaced by `effort: medium`, and `agents/openai.yaml` no longer pins a model. | |
| 2026-09-27 | Re-synced this repo's copy wholesale to `smooth-devex-template` `main` (PR #84 and the 2026-09-23 row above had not been ported); the local deltas are the `SmoothAiProductContextMemory` backend path prefix and the `SKILL.md` Overview sentence, which here states the Project add and parent link as conditional (`--no-project`/`--noparentid`, `--feature-issue`) — the template's wording implies a parent link by default (PR #122 auto-fix; worth upstreaming). Replaces a locally authored `--noparentid` that also rejected `--project`/`--org` — the template's contract (exclusive with `--feature-issue` only) wins. | template PR #84 |
