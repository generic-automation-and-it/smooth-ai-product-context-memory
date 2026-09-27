---
name: agile-github-task-from-diff
description: Create a GitHub Task (sub-issue) from the current git diff vs main and link it as a sub-issue of a parent Feature in the local GitHub Project. Use when Codex needs to summarize branch changes into a horizontally sliced task with acceptance criteria and create it via `gh`. NOT for braindumps, ideas, meeting transcripts, or breaking a Feature into Tasks — use agile-github-breakdown, which sources issues from contextual knowledge, never a diff. NOT for opening a pull request — use git-commit-push-pr.
allowed-tools:
  - Bash(.agents/skills/agile-github-task-from-diff/scripts/create_github_task_from_diff.py:*)
  - Bash(python3 .agents/skills/agile-github-task-from-diff/scripts/create_github_task_from_diff.py:*)
  - Bash(git fetch:*)
  - Bash(gh auth status:*)
  - Read
effort: medium  # diff analysis + issue authoring across layers
---

# Task From Diff

## Overview

Generate a GitHub **Task** issue from the current branch diff versus main. The task is horizontally sliced — scoped to one technical layer (backend, tests, docs, ai-tooling, config) — and, unless `--no-project` or `--noparentid` is passed, added to the local GitHub Project; it is linked as a sub-issue of a vertically sliced parent **Feature** issue only when `--feature-issue` is given. With `--noparentid` the issue is created in the repo only: no project, no parent Feature.

## Workflow

1. Ensure the repo has an up-to-date `origin/main` (or override the base ref).
2. Run a dry run to review the generated title, body, and acceptance criteria.
3. Create the task issue, add it to the project, and optionally link it as a sub-issue of the Feature — or, with `--noparentid`, create it in the repo only (no project, no parent).
4. Rename the current branch to match the `<type>/<issue>-short-description` naming standard using the newly created issue number — see [Rename Branch After Creation](#rename-branch-after-creation).

## Script

Use `scripts/create_github_task_from_diff.py`.

```bash
# Preview (dry run)
python3 .agents/skills/agile-github-task-from-diff/scripts/create_github_task_from_diff.py --dry-run

# Create task, add to project #1, link as sub-issue of Feature #42 (number or issue URL)
python3 .agents/skills/agile-github-task-from-diff/scripts/create_github_task_from_diff.py --feature-issue 42

# Repo-only task: no GitHub Project, no parent Feature
python3 .agents/skills/agile-github-task-from-diff/scripts/create_github_task_from_diff.py --noparentid

# Combine flags from the Inputs table as needed, e.g.:
python3 .agents/skills/agile-github-task-from-diff/scripts/create_github_task_from_diff.py \
  --feature-issue 42 --base-ref main --title "Add persistence layer tests" --open
```

## Inputs

| Flag | Default | Description |
|------|---------|-------------|
| `--feature-issue` | _(none)_ | Parent Feature issue — accepts an issue **number** (`42`) or a full GitHub issue **URL** (`https://github.com/.../issues/42`). When set, the task is linked as a sub-issue. |
| `--title` | _(generated)_ | Override the auto-generated task title. |
| `--repo` | _(from remote)_ | GitHub repo as `owner/repo`. Auto-detected when omitted. |
| `--project` | `1` | GitHub project number under the org. |
| `--no-project` | — | Create the issue only; skip adding it to any GitHub Project. A `--feature-issue` parent is still linked. |
| `--noparentid` | — | Repo-only task: skip the GitHub Project **and** the parent Feature link. Mutually exclusive with `--feature-issue` — combining them exits `2` before anything is read or created. |
| `--org` | _(repo owner)_ | GitHub org that owns the project. Defaults to the repo owner detected from the git remote. |
| `--label` | `task` | Label applied to the created issue. |
| `--base-ref` | `origin/main` → `main` | Override base ref for the diff. |
| `--dry-run` | — | Print output without creating anything. |
| `--open` | — | Open the created issue in the browser. |

## Behavior

- Computes the diff from merge-base between `HEAD` and the base ref.
- Classifies the diff into **horizontal layers**: `backend`, `tests`, `documentation`, `ai-tooling`, `config`, `general`.
- Builds a title from the detected layers and affected top-level areas.
- Generates an acceptance criteria checklist based on touched paths.
- Checks the `--label` exists (read-only `gh api` GET); `--dry-run` reports `Label '<name>': present|MISSING|UNKNOWN`.
- Creates the issue via `gh issue create`.
- Adds the issue to the GitHub Project via `gh project item-add` (skipped by `--no-project` and `--noparentid`).
- Links the issue as a sub-issue of the parent Feature (only when `--feature-issue` is set) via the GitHub REST API: resolves the new issue's database `id` (`gh api /repos/{owner}/{repo}/issues/<n>`), then `gh api --method POST /repos/{owner}/{repo}/issues/<parent>/sub_issues -F sub_issue_id=<id>`.

## Rename Branch After Creation

After the task issue is created, rename the **current local branch** so it conforms to the
`<type>/<issue>-short-description` standard enforced by the
[`git-commit-push-pr`](../git-commit-push-pr/SKILL.md) skill and `.agents/rules/git/git-policy.instructions.md`
(the source of truth). This guarantees the downstream PR title and `Closes #<issue>` link can be derived
from the branch name.

The script prints a ready-made suggestion after creating the issue (`Suggested branch rename: git branch -m <type>/<issue>-<slug>`) — it derives `<type>` from the **highest-precedence layer present** (`backend` > `tests` > `documentation` > `ai-tooling` > `config` > `general`) — not the largest one — `<issue>` from the new issue number, and the slug from the task title. Only `backend` maps to `feat`, so a change that adds a capability outside `src/` is never suggested as `feat`. Run that command (no commit/push is performed by this skill). Override the `<type>` if the diff is better described by `fix`/`refactor` than the layer mapping suggests.

Notes:

- **Skipped on `--dry-run`** — nothing is created, so there is no issue number to rename against.
- If a conforming `<type>` or slug cannot be determined, **stop and ask the user** — do not guess, matching the format-enforcement rule in `git-policy.instructions.md`.
- If the branch was already pushed, the rename stays local; the subsequent `git-commit-push-pr` run pushes the renamed branch (its `--issue` handling parses the same number back out of the branch name).

## Vertical vs Horizontal Slicing

| Concept | Owner | Description |
|---------|-------|-------------|
| **Feature** (parent) | Product | Vertically sliced — one complete user-facing capability across all layers. |
| **Task** (this skill) | Engineer | Horizontally sliced — one technical layer of work from the diff (e.g. just the backend changes, or just the test changes). |

## Requirements

- `gh` CLI authenticated with a token that has `repo` and `project` scopes.
- `git` available in the repo.
- The `task` label should exist in the target repo. When it is missing the issue is created unlabeled and the script prints the fix command — see [Gotchas](#gotchas).

## Gotchas

- **Sub-issue API takes the database `id`, not the issue number.** `gh issue create` returns only a URL, so the script does a second read-only GET for `.id` and posts it with `-F` (integer-typed); `-f` would send a JSON string. A "sub-issue link failed" warning now carries `gh`'s stderr and signals a real problem worth reading.
- **Missing label does not block creation.** A 404 on the label check creates the issue unlabeled, prints `gh label create <label> --repo <owner>/<repo> --color 0075ca`, and exits `0`. The script never creates the label. Run that command only after the user explicitly confirms; it is deliberately left out of `allowed-tools` so the permission prompt stays. Any other check error (auth, network) is reported and the label is still applied.
- **Script output lines are the source of truth.** Trust `Created task issue #…`, `Added to project …` and `Linked as sub-issue …` over an immediate `gh issue view` read-back — project membership and sub-issue relations can lag behind the write.
- **Only committed changes are diffed** (`merge-base..HEAD`). Staged, unstaged, and untracked work is invisible to the task; commit it first or the task under-describes the branch.
- **`allowed-tools` pre-approves, it does not restrict.** It lists only the script and read-only commands. The branch rename (`git branch -m`) and `gh label create` still go through the normal permission prompt.

## Troubleshooting

- If `gh issue create` fails, run `gh auth status` to verify authentication.
- If `gh project item-add` fails, ensure your token has the `project` scope (`gh auth refresh -s project`).
- If the sub-issue API call fails, read the printed `gh` error, then link manually in the GitHub UI using the fallback message.
- If the label check reports `UNKNOWN`, the `gh` error is printed beside it; fix auth/network and re-run the dry run.
- If the diff is empty, ensure your branch contains committed changes against main or override `--base-ref`.
