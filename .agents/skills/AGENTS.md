# .agents/skills — AGENTS.md

## TL;DR

First-party AI agent skills. They legitimately run shell, `gh`/`git`, and template file operations. This repository no longer has the former SkillSpector gate or baseline; review security-sensitive skill changes directly and run a local secret scan.

## Non-Negotiables

- **Secrets go through the environment, never into text.** Any skill needing a secret MUST follow `.github/instructions/skills/skill-secret-handling.instructions.md`: a script reads the value from a runtime environment variable; the value never appears in `SKILL.md`, prompts, agent YAML, README, or any committed file. Local context-memory and dossier clients consume runtime API tokens.
- **Review actual capabilities.** If a security scanner flags legitimate skill behavior (shelling out to `gh`, swapping a symlink, refreshing a template directory), investigate and document it; do not gut a skill to silence a finding.

## Architecture Decisions

### LADR-001 — Historical SkillSpector gate: deterministic static scan; LLM semantic stage advisory

- **Date:** 2026-06-21 · **Status:** Retired (workflow removed 2026-08-29)
- **Context:** The accepted-findings baseline is built from a static (`--no-llm`) scan. When the LLM semantic stage runs (provider/model supplied via org settings), its analyzers surface **additional, nondeterministic** findings whose ids/locations are not in the static baseline. Those count as ACTIVE and fail the gate — so a model or prompt change re-breaks the gate even though no skill changed.
- **Decision at the time:** The gate ran a **deterministic static scan (`--no-llm`)** whose baseline-aware decision was authoritative. The **LLM semantic stage ran as a separate non-blocking advisory scan** (`continue-on-error`, self-skips with no key); its findings were rendered in the job summary via `--advisory` and **did not affect the gate decision**.
- **Consequences then:** The gate was stable across model/prompt drift and needed no LLM key. The workflow and its baseline have since been removed; this decision is retained only as history and supplies no present security gate.

## Key Behaviors

- Historical SkillSpector gate details in LADR-001 describe a removed workflow, not a current PR gate. For current skill changes, inspect the diff and run a local secret scan; do not imply the former gate still runs.
- **Skills pick an effort level, never a model.** Every `SKILL.md` frontmatter is `name`, one-line `description`, optional block-list `allowed-tools`, then `effort` (`low` | `medium` | `high` | `xhigh` | `max`) — the shape and per-skill levels are tabled in `README.md` → Effort. There is no per-provider model block and `agents/openai.yaml` carries no `model:`: the session's model runs every skill, and a sub-skill is invoked at its own `effort`. Re-adding a `models:` block or a per-runner model hint reintroduces the provider coupling this removed. `max` is deliberately unused by any skill — it is the user's explicit escalation. Exception: the `smooth-ai-report-review` skills (`ai-review`, `git-commit-review-push`) stay byte-identical to that parent, including `ai-review`'s `switches:` list.
- A tool-using model launched by a skill must receive only the credentials it needs and be fenced from checkout metadata and secret files, as described in the secret-handling rule. The local context-memory clients are not model-launching skills.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-27 | `mimisbrunnr-ymir-bootstrap` added at `effort: xhigh` in `README.md` → Effort. The Naming & Ordering table gained the two skills it was missing (`ai-understanding`, `mimisbrunnr-saga-dossier`), and the Quick Reference bootstrap row dropped its one-off link to match its siblings. | skill effort migration |
| 2026-09-27 | Synced template PR #85's secret-handling checklist, marked the removed SkillSpector gate historical, and documented the local runtime-token consumers. | template PR #85 |
| 2026-06-21 | Initial version — documents the SkillSpector baseline gate contract and the secret-handling guardrail for skills. | #52 |
| 2026-06-21 | LADR-001: gate on the deterministic static scan; LLM semantic stage runs as a non-blocking advisory (policy A). Resolves the static-vs-LLM baseline mismatch that failed the gate on run 27907080342. | #52 |
| 2026-07-31 | SARIF now **omits** baselined findings instead of marking them `suppressions`; the code-scanning check was red because the back end did not honor SARIF suppressions. Summary "Accepted (baselined)" section is unchanged (reads the scan output). | |
| 2026-09-14 | This-repo skills renamed: `ai-brain-dump` → `mimisbrunnr-vitsmunir-dump`; `context-memory` → `mimisbrunnr-odin-context-memory`. Upstream skills unchanged. | |
| 2026-09-27 | Every `SKILL.md` now carries YAML frontmatter: `mimisbrunnr-saga-dossier` had none (listed with its bare name as description). `agile-github-task-from-diff` gained `--noparentid` (repo-only task). | |
| 2026-09-27 | Per-provider model selection removed from every skill. The `models:` block (`claude`/`copilot`/`codex`) is replaced by a single `effort:` level on the standard harness scale `low`→`medium`→`high`→`xhigh`→`max`; `max` is never a skill default. `agents/openai.yaml` lost its `model:` line, and the git sub-skill chain now passes no model — sub-agents inherit the session model and run at the sub-skill's effort. `README.md` "Model Selection" became "Effort". | |
| 2026-09-27 | Aligned with upstream `smooth-ai-report-review` PR #167 (master of the shared skills): `ai-review` → `effort: high`, `git-commit-review-push` → `effort: low` with its "Low effort, not low care" section (both byte-identical to upstream `main`); `README.md` section renamed "Effort Selection" with upstream's wording; every `effort:` comment uses upstream's column alignment. | upstream PR #167 |
| 2026-09-27 | Aligned with `smooth-devex-template` PR #84 (parent of most skills here): frontmatter normalised to its shape (one-line `description`, block-list `allowed-tools`, `effort`; `switches:` dropped from template-shaped skills), template effort levels adopted — `git-sync` low→medium, `ai-terse` low→medium, `ai-template-sync` high→medium, `mimisbrunnr-vitsmunir-dump` high→medium, `agile-github-breakdown` high→xhigh — with the template's rationale comments; `agile-github-task-from-diff` re-synced wholesale; `README.md` → Effort follows the template's section (scale, per-skill table, sub-skill invocation, frontmatter shape); the worktask template recommends a session effort instead of a model. | template PR #84 |
