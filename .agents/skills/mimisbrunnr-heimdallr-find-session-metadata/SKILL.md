---
name: mimisbrunnr-heimdallr-find-session-metadata
description: Scan the current session for binding metadata — tickets, repository, initiative — and print it to the console. Use when an export needs its binding without guessing, or when a practitioner asks what ticket, repo or initiative this session belongs to. Triggers on "session metadata", "what ticket is this", "find session binding", "what repo am I in".
effort: low  # script-driven git inspection; no reasoning, no store touch
---

# Heimdallr Find Session Metadata

Heimdallr watches the Bifrost and hears grass grow — this skill watches the session and
reports what it can prove. It looks at the **current checkout** for the three binding
values an `export` needs — **tickets, repository, initiative** — and prints them to the
console. Offline, read-only, no store call, no secret read.

## Run

From the repository root:

```bash
python3 -B .agents/skills/mimisbrunnr-heimdallr-find-session-metadata/scripts/find_session_metadata.py [--json] [--initiative NAME] [--repo-root DIR]
```

`--repo-root DIR` scans that checkout (`git -C DIR`) instead of the working directory. Use it whenever the
repository you are binding is not the one the session runs in, and check `rootMatches` before accepting
anything from the scan: it answers whether the scanned checkout is the requested one, while `root` is for
display only and is `null` when withheld.

## Sources (and only these)

| Field | Source | Example |
|---|---|---|
| `repository` | `git remote get-url origin`, parsed to `owner/repo`; `null` when withheld | `generic-automation-and-it/smooth-ai-product-context-memory` |
| `repositoryWithheld` | Why the origin path is not shown (credential-shaped, or no redactor to check it), else `null` | `null` |
| `tickets` | Current branch name + recent commit subjects (the branch ref, `HEAD` when detached; last 10), matched for `#123`, `provider:key`, `JIRA-123` shapes | `github:160` from `feat/160-...` |
| `initiative` | `--initiative NAME` flag only, else `unknown` | never guessed from prose |
| `branch` | `git branch --show-current` (reported, not a binding); `null` when withheld | `feat/160-...` |
| `branchWithheld` | Why the branch name is not shown (credential-shaped, or no redactor to check it), else `null` | `null` |
| `root` | `git rev-parse --show-toplevel` — which checkout was scanned, shown from `~` under the home folder; `null` when withheld | `~/work/smooth-ai-product-context-memory` |
| `rootWithheld` | Why the checkout path is not shown (outside the home folder, credential-shaped, or no redactor to check it), else `null` | `null` |
| `rootMatches` | With `--repo-root`: whether the scanned checkout's top level is that directory (resolved paths); else `null` | `true` |
| `ticketsWithheld` | Count of credential-shaped candidates dropped (values never shown) | `1` |
| `ticketsUnavailable` | Why no ticket is reported at all (the redactor could not be loaded), else `null` | `null` |
| `commitsUnavailable` | Why recent commit subjects were not read (`git log` failed and the branch is not provably unborn), else `null`; tickets then come from the branch alone. Only a provably unborn branch (HEAD reads as a symbolic ref to it and it has no ref) is an empty history, not a failure | `null` |

## Rules

- **Console only.** Prints human lines by default, JSON only with `--json`. Writes no file, touches no store.
- **Never guess the initiative.** A branch, a folder name and a commit message are not an initiative. Without the flag it is `unknown` and the export binds by nothing.
- **Never open `.context/`, `.env*` or `*.env`.** The provisioned token file lives in the working tree; discovery stays on git output.
- **Prose is evidence, not binding.** A ticket-shaped string in a commit message is reported with its source; nothing is promoted to a binding without the operator.
- **Credential-shaped candidates are withheld.** `provider:key` matches any `word:value`, so every candidate
  passes the capture skill's redactor (`mimisbrunnr-odin-context-memory/scripts/redact.py`): one whose
  provider is a credential word (`password`, `token`, `API_KEY` …), or whose text — alone or in its subject —
  the redactor would change, is dropped and only counted. The branch name is withheld (`branchWithheld`) when
  the redactor would change it or a candidate inside it was dropped. Without the redactor no ticket, no branch name, and no repository is reported.
- **Deduped, sourced.** Each ticket is listed once with where it was seen (branch, commit subject). No ordering beyond first-seen.
