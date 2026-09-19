---
description: 'Pull request standards — GitHub PR workflow, Conventional Commits title format, AI Review Notes'
globs: "**"
paths:
  - "**"
applyTo: '**'
alwaysApply: true
---
# Pull Request Standards

**All PRs MUST follow the Conventional Commits title format.** Updated: 2026-05-30

## PR Title Format

Follows [Conventional Commits](https://www.conventionalcommits.org) — see `git-policy.instructions.md` for full details.

`<type>[optional scope]: <description>`

Examples: `feat(skills): add github-task-from-diff skill` | `fix(hooks): resolve slnx path detection on Windows`

## Branch Naming

Pattern: `<type>/<ticket-or-slug>-<description>` (lowercase, hyphens). Branch `<type>` uses the **same Conventional Commits vocabulary** as commit/PR-title types — see `git-policy.instructions.md`.

| Valid Types | Invalid Types |
|-------------|---------------|
| `feat/`, `fix/`, `chore/`, `docs/`, `refactor/`, `test/`, `ci/`, `perf/`, `build/` | `feature/`, `bugfix/`, `hotfix/`, `maintenance/` |

## PR Creation Checklist

1. **Get metadata** from branch name: type, scope, title
2. **Use `gh pr create`** — this is a GitHub repository; use the `gh` CLI
3. **PR template**: use `.github/pull_request_template.md` if present, otherwise write a clear description with bullet points
4. **Fill sections**: Description (bullet points), Type of Change, Testing notes
5. **AI Review Notes** (mandatory): focus areas and context
6. **Skip Areas / Known Issues** (mandatory when anything is knowingly skipped): a separate
   top-level section, not a bullet inside AI Review Notes — the gate parses it

## PR Update Requirements

1. Analyze COMPLETE changeset (`git diff <base>...HEAD`), not just latest commit
2. FULL REPLACEMENT of description based on actual changes (all commits, not incremental)
3. Preserve existing AI Review Notes (enhance, never delete)
4. Preserve PR title unless scope fundamentally changed

## AI Review Notes Example

Two **sibling top-level** sections. `Skip Areas / Known Issues` is not a sub-heading of
`AI Review Notes` — see [Skip Areas is a parsed section](#skip-areas-is-a-parsed-section).

```markdown
## Skip Areas / Known Issues

- `src/Api/PaymentEndpoint.cs:88` duplicate validation — intentional for backward compatibility

## AI Review Notes

**Focus Areas:**
- Verify migration is backward compatible
- Check error handling in payment flow

**Context:**
- Hotfix for production issue
- TODO on line 45 addressed in follow-up #4567
```

## Skip Areas is a parsed section

The review gate does not read the PR description as prose. `extract-review-notes.sh`
greps two **top-level** headings — `^## AI Review Notes` and `^## Skip Areas` — and
terminates each section walk at the next `^## `. A `**Known Issues:**` bold line nested
inside `## AI Review Notes` is therefore not a heading at all and never reaches the
review prompt, so every finding marked `skip` is re-raised on the next round.

Rules that follow from that:

- Keep the literal words `Skip Areas` in a `## `-level heading. Demoting it to `###`
  or folding it under another section makes it invisible.
- Anchor each bullet with `<file>:<line>` plus the reason, so the next round can match
  it against a finding.
- Verify by round-trip, never by eye. The lib lives in the gate's runner-only
  `.review-tools/` checkout, so fetch it at the pinned SHA first:

  ```bash
  gh api "repos/generic-automation-and-it/smooth-ai-report-review/contents/\
  .agents/skills/ai-review-report/scripts/lib/extract-review-notes.sh?ref=4bdfea4f361218d88745dfcbad0b00a108a129f2" \
    --jq .content | base64 -d > /tmp/extract-review-notes.sh
  gh pr view <n> --json body --jq .body | bash /tmp/extract-review-notes.sh
  ```

  Both sections must appear. The same check runs against
  `.github/pull_request_template.md` whenever that file is edited. More generally: when you change a file a script reads, run that script against
  the changed file before calling it done.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change |
|:-----|:-------|
| 2026-05-30 | Initial version. |
| 2026-05-30 | Align branch-name types with Conventional Commits (`feat`/`fix`/… valid; `feature`/`bugfix`/… invalid). |
| 2026-09-19 | Example split into sibling `## Skip Areas / Known Issues` + `## AI Review Notes` sections, and the parser contract behind that split documented. The nested `**Known Issues:**` shape it replaced was invisible to `extract-review-notes.sh`. |
