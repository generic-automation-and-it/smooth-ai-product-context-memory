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
python3 -B .agents/skills/mimisbrunnr-heimdallr-find-session-metadata/scripts/find_session_metadata.py [--json] [--initiative NAME]
```

## Sources (and only these)

| Field | Source | Example |
|---|---|---|
| `repository` | `git remote get-url origin`, parsed to `owner/repo` | `generic-automation-and-it/smooth-ai-product-context-memory` |
| `tickets` | Current branch name + recent commit subjects (`HEAD`, last 10), matched for `#123`, `provider:key`, `JIRA-123` shapes | `github:160` from `feat/160-...` |
| `initiative` | `--initiative NAME` flag only, else `unknown` | never guessed from prose |
| `branch` | `git branch --show-current` (reported, not a binding) | `feat/160-...` |

## Rules

- **Console only.** Prints human lines by default, JSON only with `--json`. Writes no file, touches no store.
- **Never guess the initiative.** A branch, a folder name and a commit message are not an initiative. Without the flag it is `unknown` and the export binds by nothing.
- **Never open `.context/`, `.env*` or `*.env`.** The provisioned token file lives in the working tree; discovery stays on git output.
- **Prose is evidence, not binding.** A ticket-shaped string in a commit message is reported with its source; nothing is promoted to a binding without the operator.
- **Deduped, sourced.** Each ticket is listed once with where it was seen (branch, commit subject). No ordering beyond first-seen.
