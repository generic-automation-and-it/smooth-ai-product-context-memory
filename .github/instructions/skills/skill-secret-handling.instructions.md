---
description: 'How AI agent skills must handle secrets — read from the runtime environment via a script, never embed secret values in model-visible or committed text.'
globs: ".agents/skills/**"
paths:
  - ".agents/skills/**"
applyTo: '.agents/skills/**'
alwaysApply: false
---

# Skill Secret Handling

How any skill under `.agents/skills/` must handle a secret (API key, token, password, connection string). Updated: 2026-09-27

## The Rule

A skill that needs a secret **MUST delegate to a script that reads the secret from the runtime environment** (an environment variable injected at execution time) and uses it there. The secret **value** must never appear in any model-visible or committed text.

| Allowed | Forbidden |
|---------|-----------|
| `SKILL.md` instructs the agent to run a script that reads `$MY_API_KEY` from the env | A real key, token, or password written literally in `SKILL.md`, a prompt, agent YAML, README, reference doc, or any committed file |
| A bash/python script reads the secret via `os.environ` / `"$VAR"` and passes it to the tool | Echoing/printing the secret, putting it in a URL query string, or passing it as a logged CLI argument |
| Documenting the env var **name** the script expects (e.g. `MY_API_KEY`) | Documenting the env var **value** |

The secret value flows: **runtime environment → script → tool**. It is never typed into a file an agent reads, generates, or commits.

## Reference Pattern

The context-memory clients read `CONTEXT_MEMORY_READ_TOKEN` or `CONTEXT_MEMORY_WRITE_TOKEN` inside their Python process and attach the selected value to an HTTP authorization header. The read-only client refuses to start with the write token present. Mirror this shape for any skill needing a secret: declare the env var name, read it in a script, limit which process receives it, and never persist the value.

## Checklist

Run this when **authoring or reviewing** a skill that touches a secret or launches a model/agent with tools. Every box is a yes/no question about the diff; a "no" is a finding, not a style note.

**Where the value lives**
- [ ] No secret value in `SKILL.md`, prompts, agent YAML, README, references, templates, tests or fixtures — only env var **names**.
- [ ] Knowledge artefacts the skill writes (Understandings, worktasks, PR bodies, run summaries) redact to `<REDACTED>`.
- [ ] Custom config ships placeholders (`{env:VAR}`), never values.

**How the script uses it**
- [ ] Read from the environment inside a script (`"${VAR:-}"`, `os.environ`); presence checked without printing (`[ -n "${VAR:-}" ]`).
- [ ] Never a CLI argument, URL userinfo (`https://token@host`) or query string. Use env-auth tools (`gh` reads `GH_TOKEN`), stdin, or a one-shot credential helper. For Git pushes where the exact token matters, use `.github/scripts/git-credential-from-env.sh`: a local probe found `gh auth git-credential` returned a stored token instead of the supplied synthetic `GH_TOKEN`.
- [ ] A user-supplied URL is **rejected** when it embeds credentials, before it is echoed or cloned.
- [ ] No `set -x`, `env`/`printenv`, or unredacted stderr from tools that echo URLs or headers around the secret.

**What a model process can reach**
- [ ] The model subprocess gets only the key its provider needs, via an **allowlist** (a deny-list misses tokens nobody listed, such as a developer shell's other keys): in a subshell, `unset` every variable not on the list, then `exec` the model CLI. A GitHub, OIDC or cloud token never reaches it. Never pass `NAME=value` to `env -i`: the value shows in the process list.
- [ ] Tool sandbox: no shell, no web fetch, no paths outside the repo; `read` and `edit` deny `.git/**` (persisted credentials, hooks) and `.env*`. Check **how** each tool's permission is matched: a content-search tool whose rule matches the query, not the file path (opencode `grep`), cannot be fenced by path and must be denied outright. Agent-level `read: "allow"` replaces the tool's default `.env` deny, so restate the denies.
- [ ] Nothing on disk inside the model's readable root holds a credential while the model runs. In Actions: `actions/checkout` with `persist-credentials: false`. If one may still be there, the script moves it outside the root for the model phase and restores it (or fails closed when it cannot safely move it).
- [ ] **Every** git command the script runs is hook-proof, set once for the whole script (`GIT_CONFIG_COUNT`/`GIT_CONFIG_KEY_n=core.hooksPath`/`GIT_CONFIG_VALUE_n=/dev/null`), not per command: the model may have written a hook, hooks inherit the token, and `checkout`, `commit`, `push` and any ref update each fire one.
- [ ] Untrusted input the model reads (upstream repos, issues, diffs) is treated as a prompt-injection source. The script, not the model, decides what gets committed or pushed.

**CI wiring**
- [ ] Secrets mapped on the **step** that needs them, not job-level `env`.
- [ ] Only the needed secrets are mapped; `secrets: inherit` is documented as a same-org shortcut, not the default.

**Before the PR**
- [ ] Run a secret scan and inspect changed scripts, model subprocesses, and workflow credential scope before the PR. This repository has no SkillSpector gate or baseline; do not claim one ran.
- [ ] The skill's `AGENTS.md` names every env var it reads, and which process receives it.

## Current Status

**Several local skills handle runtime tokens:** `mimisbrunnr-odin-context-memory` (read/write API tokens), `mimisbrunnr-saga-dossier` (read token), and their supporting client processes. `ai-template-sync` reads no secret but refuses a `--template-url` with embedded credentials. `mimisbrunnr-ymir-bootstrap` reads no secret either: store access is delegated to the context-memory workers, it refuses credential-bearing evidence URLs, and it redacts secrets met in evidence to `<REDACTED>` in its preview. This repository does not include the template's `ai-asset-sync` skill or SkillSpector gate. The model-process checklist applies whenever a skill launches a tool-using model; it is not a claim that these clients do so.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change |
|:-----|:-------|
| 2026-09-27 | Current Status names `mimisbrunnr-ymir-bootstrap` as a no-secret skill that delegates store access and refuses credential-bearing evidence URLs. |
| 2026-09-27 | Named the tested one-shot Git helper for exact-token pushes after a local `gh auth git-credential` precedence probe. |
| 2026-09-27 | Synced the template PR #85 checklist into the `skills/` category and adapted examples, status, and scan instructions to this repository's actual token consumers and tooling. |
| 2026-08-29 | Dropped the SkillSpector workflow example after that workflow was removed. |
| 2026-06-21 | Initial version — env-via-script secret handling for skills. |
