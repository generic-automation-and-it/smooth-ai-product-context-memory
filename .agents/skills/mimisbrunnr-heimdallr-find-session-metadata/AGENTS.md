# mimisbrunnr-heimdallr-find-session-metadata — AGENTS.md

## TL;DR

Offline console reporter for the three values an `export` binds by: tickets, repository,
initiative. Reads git output only (remote, branch, recent subjects); no store call, no
secret read, no file written.

Heimdallr watches and reports — he does not judge or bind. The operator binds.

## Non-Negotiables

- **Console only.** Human lines by default, JSON with `--json`. No file write, no store touch.
- **Never guess the initiative.** `--initiative` flag or `unknown`. A branch or folder name is not one.
- **Never open `.context/`, `.env*`, `*.env`.** The provisioned token file lives in the working tree.
- **Prose is evidence, not binding.** Ticket-shaped strings are reported with their source; nothing is promoted silently.
- **No network, no credentials.** The script runs no remote command and reads no token variable.

## Key Behaviors

- `#123` is GitHub by convention (shared with the understanding export); `provider:key` keeps its provider lowercased; bare `ABC-123` is `local`.
- Tickets deduped in first-seen order, branch before commits, and by character offset within one
  branch or subject — never by which pattern matched (`fix ABC-123 and #456` lists `ABC-123` first).
- The commit window is pinned to the branch ref (`git log <branch>`), falling back to `HEAD` when
  detached, so the same branch state always yields the same subjects — the report is reproducible.
- Unprovable fields read `unknown` with no source, never an empty string dressed as an answer.
- Python 3.9 compatible, stdlib only.

## Test References

- `tests/run_tests.py` — stdlib unittest, fake `git` on `PATH`. Covers repo parsing (https/ssh/scp shapes, unprovable), ticket extraction (branch `#123`, `provider:key`, bare `ABC-123`, dedupe with source, first-seen order within one subject), initiative default/flag, and the console-only contract (no file created, `--json` parses).
- Run: `python3 -B .agents/skills/mimisbrunnr-heimdallr-find-session-metadata/tests/run_tests.py`.
- **CI runs this harness** — `.github/workflows/pr-gate.yml` `python-harnesses` ("Test session-metadata skill") on the 3.9 and 3.12 legs. The harness itself must stay 3.9-importable: it carries `from __future__ import annotations` for its `str | None` hints.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-05 | **Tickets within one subject are reported in first-seen order, and the harness runs on Python 3.9 and in CI.** `find_tickets` iterated pattern by pattern, so `fix ABC-123 and #456` listed `#456` first, contradicting the first-seen contract; matches are now collected with their offsets and reported in source order, with the provider-span suppression of a bare key inside `provider:key` unchanged. The harness's `str \| None` annotation raised `TypeError` at import on 3.9, so none of its checks could run on the declared floor; it now postpones annotation evaluation like the script. It was also absent from the PR gate — a `Test session-metadata skill` step and a path filter for this skill were added. Harness 14 -> 15. | issue 179 |
| 2026-10-03 | Several correctness fixes. `find_tickets` no longer re-emits a bare-key-shaped KEY (`ABC-123`) inside a `provider:key` as a spurious `local:ABC-123` — the dedupe is keyed on `provider:key`, so the two idents never collapsed; a bare key on its own is still `local`. `parse_repo` drops an SSH port (`ssh://git@host:2222/group/repo`) and a trailing query/fragment (`repo.git?ref=x`), so neither becomes a path segment or defeats the match. And the "writes no files" harness test now checks the directory the script actually runs in (it previously inspected an untouched temp dir along the script's own `run_with_git` inner cwd), so it can no longer pass on a regression. | session report |
| 2026-10-03 | Two correctness fixes. (1) `parse_repo` now captures **all** remote path segments instead of the last pair, so a GitLab `group/subgroup/repo.git` produces `group/subgroup/repo` rather than a silently-wrong `subgroup/repo`; the host is stripped per URL form (https and scp/ssh) and `.git` before matching. (2) `scan` probes `git rev-parse --is-inside-work-tree` and raises `GitUnavailable` (a non-git checkout or a missing git binary), which `main` turns into an exit-2 with a `git unavailable: …` stderr line — a caller now distinguishes "this repo has no tickets" from "git cannot run here", instead of both reading as an empty recall. Both pinned by new harness tests. | git metadata correctness |
| 2026-10-03 | **Heimdallr removed from kvasir `import`; autofill is outbound only.** Kvasir `export`/`dump` and dossier `bundle` call it via a skills-root-relative lookup (`--heimdallr true` default, `false` opts out); odin/vitsmunir/ymir follow it as agent guidance. `import` takes only what the caller binds. Contract unchanged: still git-only repo/tickets/initiative to console, still never tags (keyword-derived by the agent) and never `unknown`-as-binding. | session request |
| 2026-10-03 | **Commit window pinned to the branch ref** (`git log <branch>`, `HEAD` when detached). The implicit-HEAD log read whichever tip the checkout sat on, so the same branch state could yield a different ticket between invocations. Harness 8 -> 9 (byte-identical stability test). | this PR |
| 2026-10-03 | **Heimdallr is now the default autofill source for the five labelling skills.** Kvasir `import`/`export`/`dump` and dossier `bundle` call it via a skills-root-relative lookup (`--heimdallr true` default, `false` opts out); odin/vitsmunir/ymir follow it as agent guidance. Contract unchanged: still git-only repo/tickets/initiative to console, still never tags (keyword-derived by the agent) and never `unknown`-as-binding. | session request |
| 2026-10-02 | Created — offline git-only session-metadata reporter (tickets, repository, initiative) to console. Heimdallr: the watcher who sees all, fitting a scanner that reports without binding. Draft. | session request |
