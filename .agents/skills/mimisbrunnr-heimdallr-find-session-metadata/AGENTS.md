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
- **No unchecked ticket.** Every candidate passes the capture skill's redactor, loaded from
  `../mimisbrunnr-odin-context-memory/scripts/redact.py` by file path (never `import redact`, so a
  same-named module cannot stand in). A candidate is dropped when `is_credential_key(provider)` is true or
  `scrub_located` would change the candidate alone or a span of its subject that overlaps it; a dropped
  candidate is counted in `ticketsWithheld` and its value is never printed. If the redactor cannot be
  loaded, `tickets` is empty and `ticketsUnavailable` says why — fail closed, never unchecked tickets. Any
  provider is otherwise accepted; there is no provider allowlist.
- **No unchecked branch name.** The branch is free text like a commit subject. It is `null` with
  `branchWithheld` saying why when `scrub_located` would change it, when a ticket candidate inside it was
  withheld (printing the branch would print that value), or when the redactor cannot be loaded.
- **No unchecked origin path.** `parse_repo` drops the host and userinfo but keeps every path segment, so
  the repository is `null` with `repositoryWithheld` when the redactor would change it, a segment is a
  credential word (`is_credential_key`), or the redactor cannot be loaded. kvasir and saga disclose it.
- **A failed read is not an empty answer.** A `git log` that fails is disclosed as `commitsUnavailable`
  unless the branch is provably unborn (HEAD is a readable symbolic ref to it and it has no ref of its
  own); only that case is an empty history.

## Key Behaviors

- `#123` is GitHub by convention (shared with the understanding export); `provider:key` keeps its provider lowercased; bare `ABC-123` is `local`.
- Tickets deduped in first-seen order, branch before commits, and by character offset within one
  branch or subject — never by which pattern matched (`fix ABC-123 and #456` lists `ABC-123` first).
- The commit window is pinned to the branch ref (`git log <branch>`), falling back to `HEAD` when
  detached, so the same branch state always yields the same subjects — the report is reproducible.
- Unprovable fields read `unknown` with no source, never an empty string dressed as an answer.
- `--repo-root DIR` scans that checkout with `git -C DIR`; without it the working directory is scanned.
  `root` is a `~/...` display form of the checkout read and may be withheld (`null` with `rootWithheld`; always withheld outside the home folder);
  never compare it — use `rootMatches` for checkout identity (ymir requires `rootMatches` against its
  chosen root).
- Python 3.9 compatible, stdlib only.

## Test References

- `tests/run_tests.py` — stdlib unittest, fake `git` on `PATH` (real `git` for `RepoRootTests`). Covers repo parsing (https/ssh/scp shapes, unprovable), ticket extraction (branch `#123`, `provider:key`, bare `ABC-123`, dedupe with source, first-seen order within one subject), initiative default/flag, and the console-only contract (no file created, `--json` parses). `CredentialShapedTicketTests` iterates the shared `mimisbrunnr-odin-context-memory/tests/fixtures/credential_like.json`: every credential entry (redactor-gated or not) yields no ticket and its secret appears in neither the human nor the `--json` output, every control is still a ticket, a bare key inside a `password=` assignment is withheld, and a copy of the script with no redactor beside it reports no ticket and `ticketsUnavailable`. `RepositoryRedactionTests` puts every `credential_like.json` entry into an origin path (both outputs), keeps ordinary origins visible, and withholds the repository with no redactor beside the script. `BranchRedactionTests` puts every credential fixture in the branch name (as an assignment and as a ticket shape) and requires `branch: null`, `branchWithheld`, the secret in neither output, and the commit tickets intact; an ordinary branch is still shown, and with no redactor the branch is not shown. `CommitHistoryTests` requires `commitsUnavailable` when `git log` fails on a resolvable ref, and none for a readable history or an unborn branch. `RepoRootTests` builds two real checkouts and asserts `--repo-root` scans the named one, `root` names the checkout read, a non-checkout root exits 2, a real unborn branch is an empty history rather than a failure, and a real `fix/password=…` branch is withheld.
- Run: `python3 -B .agents/skills/mimisbrunnr-heimdallr-find-session-metadata/tests/run_tests.py`.
- **CI runs this harness** — `.github/workflows/pr-gate.yml` `python-harnesses` ("Test session-metadata skill") on the 3.9 and 3.12 legs. The harness itself must stay 3.9-importable: it carries `from __future__ import annotations` for its `str | None` hints.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-07 | README: operator checks `root` and picks relevant tickets instead of passing every reported one. | review 5438563690 |
| 2026-10-06 | A checkout outside the home folder is withheld (`rootWithheld`): its absolute path can name another account. | issue 190 |
| 2026-10-06 | Root shown from `~`, withheld when credential-shaped or unredactable; `rootMatches` answers `--repo-root` without printing it. | issue 188 |
| 2026-10-06 | Origin path passes the redactor: `https://host/x-access-token/<token>/org/repo` printed the token in `repository`. | issue 186 |
| 2026-10-06 | Branch name passes the redactor (`fix/password=…` leaked); a failed `git log` is disclosed, never an empty history. | issue 184 |
| 2026-10-05 | Credential-shaped `word:value` candidates are withheld, not reported as tickets; `--repo-root` targets a named checkout. | issue 182 |
| 2026-10-05 | Tickets in one subject are reported in first-seen order. | issue 179 |
| 2026-10-03 | `provider:KEY` no longer re-emits `local:KEY`; SSH port dropped from the repository. | session report |
| 2026-10-03 | Repository keeps every path segment (`group/subgroup/repo`), not the last two. | git metadata correctness |
| 2026-10-03 | Autofill is outbound only; kvasir `import` never runs it. | session request |
| 2026-10-03 | Commit window pinned to the branch ref, so the same branch state yields the same ticket. | PR |
| 2026-10-03 | Default autofill source for the labelling skills (`--heimdallr false` opts out). | session request |
| 2026-10-02 | Created — offline git-only metadata reporter (tickets, repository, initiative). | session request |
