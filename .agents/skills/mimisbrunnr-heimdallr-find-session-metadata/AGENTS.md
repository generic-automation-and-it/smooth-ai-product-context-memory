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
- Tickets deduped in first-seen order, branch before commits.
- Unprovable fields read `unknown` with no source, never an empty string dressed as an answer.
- Python 3.9 compatible, stdlib only.

## Test References

- `tests/run_tests.py` — stdlib unittest, fake `git` on `PATH`. Covers repo parsing (https/ssh/scp shapes, unprovable), ticket extraction (branch `#123`, `provider:key`, bare `ABC-123`, dedupe with source), initiative default/flag, and the console-only contract (no file created, `--json` parses).
- Run: `python3 -B .agents/skills/mimisbrunnr-heimdallr-find-session-metadata/tests/run_tests.py`.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-03 | **Heimdallr is now the default autofill source for the five labelling skills.** Kvasir `import`/`export`/`dump` and dossier `bundle` call it via a skills-root-relative lookup (`--heimdallr true` default, `false` opts out); odin/vitsmunir/ymir follow it as agent guidance. Contract unchanged: still git-only repo/tickets/initiative to console, still never tags (keyword-derived by the agent) and never `unknown`-as-binding. | session request |
| 2026-10-02 | Created — offline git-only session-metadata reporter (tickets, repository, initiative) to console. Heimdallr: the watcher who sees all, fitting a scanner that reports without binding. Draft. | session request |
