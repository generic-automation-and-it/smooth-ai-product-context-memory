# AGENTS.md - ai-asset-sync

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — flags, manifest, env contract and guardrails live there; `run-sync.sh` comments carry the mechanics.

## Non-Negotiables

- **Each env var reaches one process.** `OPENCODE_<PROVIDER>_API_KEY` (selected provider only;
  `OPEN_ROUTER` → `OPENCODE_OPENROUTER_API_KEY`) → the opencode child, via `run_model`'s allowlist.
  `GITHUB_TOKEN`/`GH_TOKEN` → `gh` and the one-shot credential-helper push, never the model.
  `OPENCODE_AI_SYNC_*`, `OPENCODE_CLI_VERSION`, `AI_ASSET_SYNC_*` → the script (non-secret `OPENCODE_*`
  names also pass to the child). `*_PROXY` reach the child verbatim, userinfo included, on purpose:
  authenticating-proxy runners need it — don't strip it.
- **No `git config` write between set-aside and restore.** `restore_git_credentials` copies the whole
  `.git/config` back after the merge loop, so a write inside the loop is silently lost — put it after.
- **Parser changes run `scripts/lib/test_parse_manifest.py` by hand — no workflow runs it.**
  `run-sync.sh` has no tests.

## Architecture Decisions

### LADR-002 — Lockfile advances only inside a file-changing PR

- **Status:** Accepted · **Date:** 2026-09-13
- **Decision:** no lockfile-only PR; unchanged, no-op and skipped-blocker entries do not advance, so the
  next run re-resolves them.
- **Consequence:** an upstream bump the model calls equivalent re-pays a model call every run until
  another entry produces a real PR.

### LADR-004 — Credential isolation for the model process

- **Status:** Accepted · **Date:** 2026-09-27
- **Decision:** opencode launches only through `run_model` (env allowlist); the `sync` agent denies
  `read`/`edit` on `.git/**` and `.env*`, and `grep` outright; a checkout `extraheader` is set aside
  outside the repo for the ai-merge loop, then restored; a credentialed remote URL fails closed; hooks
  are off script-wide via `GIT_CONFIG_COUNT`.
- **Consequence:** default-checkout consumers need no change. **Rejected:** failing closed on a persisted
  `extraheader` — it breaks every default `actions/checkout` consumer to guard a file the script can
  move aside itself.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-09-13 | Initial: manifest/lockfile, AI-merge, composite action, reusable workflow; strict source charsets, containment-scoped staging, in-repo sandbox, clean-tree guard | #66 |
| 2026-09-27 | LADR-004: env allowlist, `.git/**`/`.env*`/`grep` denied, checkout credential set-aside, script-wide hook disable, case-insensitive `OPENCODE_*` secret check | #85 |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
