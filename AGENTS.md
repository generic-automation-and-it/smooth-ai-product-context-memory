# AGENTS.md

Guidance for AI coding agents in SmoothAiProductContextMemory.

## Overview

Persistent memory service for AI agents: stores summarized, labelled context (linked to issue/ticket numbers) for retrieval across long gaps. HTTP Docker API + an agent skill for get/set.

**Tech stack:** .NET 10 · ASP.NET Core · Clean Architecture (Domain / Application / Infrastructure / Host) · EF Core + PostgreSQL with Apache AGE · Mediator (source-gen CQRS) · xunit.v3

**Observability:** Serilog is the single logging pipeline (console + Seq → OpenTelemetry OTLP to the Aspire dashboard). Memory content never reaches a log, span attribute or metric tag (NFR-05).

## AI Context Files

`AGENTS.md` and `*AGENTS.md` are first-class AI-coder context — read like `CLAUDE.md`, not optional reference. Layered domain → sub-domain → feature → technology; read every level that governs code you touch, nearest `*AGENTS.md` most authoritative. Keep `*AGENTS.md` synced with code; every PR updates at least one. Prefer local context over adding to this root file — avoid restating the same plan at multiple levels. Changelog entries follow the same rule: record a change in the **nearest localized `*AGENTS.md`'s** changelog, not the root's — the root `## Changelog` is only for changes that are global to the solution or to this root file itself; when no localized `*AGENTS.md` exists for the touched area, the root changelog is that change's home (announcing a newly created doc area counts as solution-global).

Planned work tracked as worktasks under `.context/work-tasks/` (gitignored). Use `/create worktask`. **Never reference worktask IDs (e.g. `WT-04`) in delivered artefacts** — code, comments, `*AGENTS.md`, HLDs, changelogs. Worktasks are short-lived and gitignored; cite the durable authority instead (HLD, LADR, NFR, PR, issue).

## Repository Layout

| Layer | Path | Purpose |
|---|---|---|
| Domain | `src/SmoothAiProductContextMemory.Domain/` | Core entities, value objects — no external deps |
| Application | `src/SmoothAiProductContextMemory.Application/` | Vertical-slice use cases via Mediator — `Features/<Name>/` (contract: `Features/FEATURES_AGENTS.md`), shared code in `Common/` + `Abstractions/` |
| Infrastructure | `src/SmoothAiProductContextMemory.Infrastructure/` | EF Core + PostgreSQL+AGE (`Persistence/`), HTTP clients (`Clients/`), blob storage (`Storage/`) |
| Host | `src/SmoothAiProductContextMemory.Host/` | ASP.NET Core Web API, Serilog → console/Seq + OpenTelemetry OTLP, `/health` + `/alive`, Scalar OpenAPI |
| AppHost | `src/SmoothAiProductContextMemory.AppHost/` | Aspire dev orchestrator — Postgres+AGE (`docker.io/apache/age:release_PG17_1.7.0`) + MinIO blob storage + Seq |
| ChatHost | — | **Not in the tree.** Reserved placeholder for a future standalone LLM microservice — do not re-propose it from scratch. |
| Docs | `docs/` | Wiki, HLDs, BRDs |
| Scripts | `scripts/` | Operational verification against containers (restore round-trip, pre-upgrade check, ticket preflight, seeder, AppHost teardown, controller launchers). Local contract: [scripts/AGENTS.md](scripts/AGENTS.md). |

## Rules

Rules live under `.agents/rules/` as `*.instructions.md`, auto-loaded every session by Claude Code / Cursor / Copilot / Codex (symlinks in `.agents/AI_DEVELOPMENT_AGENTS.md`). Scoping is **per-file** frontmatter: `paths` (Claude), `globs`+`alwaysApply` (Cursor), `applyTo` (Copilot). Category subfolders are organizational only — they don't change loading. One exception: prompt-scoped rules may be deferred for Claude and re-injected on demand by a `UserPromptSubmit` hook (e.g. `code-review-standards`). See `.agents/rules/meta/rules.instructions.md`.

| Category | Folder | Contents |
|----------|--------|----------|
| _(cross-cutting)_ | `.agents/rules/` | `ai-workflow-rules`, `code-review-standards` (hook-deferred), `project-overview`, `clean-code`, `solid-principles` |
| git | `.agents/rules/git/` | `git-policy`, `pr-standards` |
| skills | `.agents/rules/skills/` | `skill-secret-handling` (secret values stay out of model-visible and committed text) |
| meta | `.agents/rules/meta/` | `rules` (file convention), `knowledge-conventional-contexts-quality` (AGENTS.md quality), `understandings` (rules vs AGENTS.md vs Understandings, and which wins) |
| backend (`**/*.cs`) | `.agents/rules/backend/` | api-mediator-validation, architecture-slices, backend-logging-conventions, external-api-clients, migrations, readonly-collections |

## Build / Test

```bash
dotnet build SmoothAiProductContextMemory.slnx                     # build
dotnet test  SmoothAiProductContextMemory.slnx                     # run all tests
scripts/provision-credentials.sh                                  # one-command credential provisioner (required by every run mode; writes .context/mimisbrunnr.env)
dotnet run --project src/SmoothAiProductContextMemory.AppHost      # Aspire: Host from working tree + postgres/blob/seq (group smooth-mímisbrunnr)
HostConfiguration__UseProject=false \
  dotnet run --project src/SmoothAiProductContextMemory.AppHost    # same stack, pull published Host image (tag may lag)
docker build -t smooth-ai-product-context-memory:local .           # Host image (see docs/wiki/docker.md)
dotnet run --project src/SmoothAiProductContextMemory.Host -- export [--output DIR] [--history] [--force]
dotnet run --project src/SmoothAiProductContextMemory.Host -- snapshot [--output DIR]  # corpus snapshot archive (tar + manifest)
dotnet run --project src/SmoothAiProductContextMemory.Host -- verify <archive>         # offline archive verification (exit 0 = clean)
dotnet run --project src/SmoothAiProductContextMemory.Host -- restore <archive> [--force]  # restore + printed reconciliation
```

Evidence harnesses (each skipped without its env var): `SMOOTH_FTS_BENCH=1` recall-tuning, `SMOOTH_FEEDBACK_BENCH=1` feedback-placement, `SMOOTH_SNAPSHOT_BENCH=1` snapshot timing, `SMOOTH_DOSSIER_BENCH=1` dossier timing, `SMOOTH_NFR_BENCH=1` NFR-01..03, `SMOOTH_AGE_BENCH=1` traversal benchmarks, `SMOOTH_WALK_BENCH=1` cold-agent walk — see the `*_BENCH` filter lines in git history. Skill harnesses: `python3 -B .agents/skills/mimisbrunnr-kvasir-understanding/tests/run_tests.py`, `.../run_walk_tests.py`, `.../mimisbrunnr-odin-context-memory/tests/run_tests.py`, `.../test_review_and_dedup.py`. Ops scripts: `seed-graph-sample.sh`, `verify-graph-restore.sh` (NFR-03), `verify-graph-preupgrade.sh` (NFR-04), `check-ticket-ownership.sh`, `smoke-apphost-container.sh`, `stop-dev-stack.sh` (keep data) vs `reset-dev-stack.sh` (destroy volumes).

Target one test project (`dotnet test tests/<Project>`) or `ls tests/` to list. Gotchas: first dashboard visit needs the printed `/login?t=...` URL; AppHost exit does not stop the stack (`mimisbrunnr-{postgres,blob-well,seq}` are Persistent — load-bearing, not a leak); never glob `mimisbrunnr-*` (also matches `mimisbrunnr-testcontainer-*`).

## Test Framework

xunit.v3 · Shouldly. Tiers (drive test placement): **L0** `*.UnitTest` (no I/O); **L1** component (real isolated Postgres via Aspire); **L2** `*.IntegrationTest` (full stack). Fixtures in `tests/SmoothAiProductContextMemory.TestFramework/`; AGE image pinned `docker.io/apache/age:release_PG17_1.7.0` (pairing: `docs/hlds/003-graph-edges-on-age/nfrs/NFR-04-version-pairing.md`). Full contract: `docs/wiki/testing.md`.

## CI/CD

- **PR gate** (`.github/workflows/pr-gate.yml`): build Release → L0 → L1 → L2, 85 % line-coverage floor, Python harnesses on 3.9/3.12, native image smoke via `scripts/smoke-apphost-container.sh`. No ruleset currently requires these checks. Full contract: `docs/wiki/ci.md`.
- **Publish image** on main pushes only (API + `-apphost` controller, one `queue: max` group). PR-only main updates through branch protection.
- **AI review gate** runs as a local job (a reusable-workflow caller cannot inject the loopback mTLS-terminator step). Live provider is in the run log (`OPENCODE-GO-OPENAI`, terminator skipped); the workflow name `PR Code Review Report` is a contract with `pipeline-ai-analyse.yml` (rename both or neither); auto-fix needs both `OPENCODE_ANALYSE_PROVIDER` and `OPENCODE_ANALYSE_MODEL`. Credential isolation: trusted-`main` guard copy, non-persisted checkouts, one-shot push helper — full contract in `docs/wiki/ci.md`.

## Git Constraints

Hosted on **GitHub** at `https://github.com/generic-automation-and-it/smooth-ai-product-context-memory`. Use `gh` for PR/repo ops. PR template: `.github/pull_request_template.md`. There is no `CODEOWNERS` file — code review is via PR review, and the org is `generic-automation-and-it`. Commit/PR formats and branch naming: `.agents/rules/git/`.

## Glossary

| Term | Description |
|---|---|
| Context | Summarized, labelled unit of knowledge stored for later retrieval |
| Understanding | A distilled, self-contained unit of hard-won knowledge — the transferable skill that remains after the experience is discarded. Stored as a memory of `kind = understanding` (BRD-003) |
| Label | Tag (e.g. issue/ticket number) linking/retrieving related contexts |
| Semantic memory | Labelled knowledge, primary store for retrieval by label/ticket, facets and full-text lexemes; free prose matches lexeme-AND, not similarity (see [`README.md`](README.md), *Where Mímisbrunnr loses*) |
| Episodic memory | Timestamped record of when/where a context was captured |
| Procedural memory | Persisted agent/user preferences and learned behaviors |

## Changelog

One-liners; full detail in git history and the linked PRs/issues.

| Date | Change | Ref |
|---|---|---|
| 2026-10-14 | **Added viewer pod foundation (Phase A)** — new `src/SmoothAiProductContextMemory.ViewerPod/` (Caddy + Python bridge stub + no-build SPA) registered in the AppHost as `mimir-viewer`; the loopback entry for the Mímisbrunnr Console (HLD-009/BRD-004). Root changelog is the record home (no localized file covers the pod; APPHOST_AGENTS.md updated alongside for the wiring). | Phase A worktask |
| 2026-10-13 | **Added `docs/wiki/what-is-special.md`** (rival comparison + differentiators) and linked it from README above Install. Root changelog is the record home (no localized file covers `docs/wiki/`). | this PR |
| 2026-10-13 | **Minimized this file.** Changelog rows condensed to one-liners; CI/CD, layout, and test sections trimmed. | PR #208 |
| 2026-10-13 | **Enabled local network binding for agents** (`network.allowLocalBinding` in `.agents/settings.json`). Root changelog is the record home (no localized file covers agent settings). | PR #208 |
| 2026-10-05 | **Capture path: review queue, closed conflict-choice menus, staged dedup** (client-side; human veto preserved; no Host/API change). | this PR |
| 2026-10-02 | **Removed the MCP layer**; read/write workers now run bash clients; no-write guarantee via read-client refusal. | MCP removal |
| 2026-10-02 | **Operator controller launchers delivered** (`scripts/run.sh` / `run.ps1`); release-port defaults; `run.sh` harnessed, `run.ps1` parse-only. | this PR |
| 2026-10-02 | **New localized context area: `.claude-plugin/AGENTS.md`** announced. | issue 164, PR 165 |
| 2026-10-01 | **MVP-readiness review closure** (redaction gate, path safety, snapshot fidelity, CI gaps, records; owner decisions left open). | this PR |
| 2026-10-01 | **README gained "Where Mímisbrunnr loses" limits section** (wording only). | ICM adoption |
| 2026-09-30 | **Cold-agent walk harness** proving memoryless agents can act on kvasir output; model-free half PR-gated. | BRD-003 §8 |
| 2026-09-29 | **Review gate forwards the seven decision-model Variables** (was logging "Decision model: off"). | issue 138 |
| 2026-09-29 | **build-and-test green (672 tests)**; rule: a review that hasn't read the check rollup hasn't verified delivery. | PR #137 |
| 2026-09-29 | **Two record-pass items explicitly declined with reason** (comment volume, file splits). | MVP completion |
| 2026-09-29 | **MVP backlog closed in one pass** (672 tests): 4 security gaps, NFR-02 re-measured 1.0/1.0, record pass. | MVP completion |
| 2026-09-28 | **Memory identity decided group-scoped** (`group, uuid`); cross-group NFR-02 evidence withdrawn. | HLD-002, LADR-01/04 |
| 2026-09-28 | **Two review findings closed as records** (preflight-judges-nothing contract; route-capability policy table). | PR #135 |
| 2026-09-27 | **Pre-MVP review loop** (bounded dossier edge load; as-of experiment reverted in CI review). | pre-MVP review loop |
| 2026-09-27 | **PR #133**: snapshot archive v3 content types, loopback-guard host parsing, CI gate hardening. | PR #133 |
| 2026-09-27 | **Fixed three stale doc surfaces** left by the credential/harness cleanup. | PR #130 review |
| 2026-09-27 | **PR #130**: credential provisioner, recall-feedback repair, route inventories, harness cleanup, setup docs. | PR #130 |
| 2026-09-27 | **Review/analyse accept `OPENROUTER_API_KEY` alias** for the namespaced secret. | upstream PR #173 |
| 2026-09-27 | **Hardened review/auto-fix credential handling** (non-persisted checkouts, one-shot push helper). | template PR #85 |
| 2026-09-27 | **Skill secret-handling rule moved** into the `skills/` category. | template PR #85 |
| 2026-09-27 | **Fixed `## Git Constraints`** (wrong repo slug, nonexistent CODEOWNERS promise). | PR #120 review |
| 2026-09-25 | **Simplified analyse "Commit fixes" step** (bare `git add -A -- .` skips ignored dirs; supersedes prior fix). | `pipeline-ai-analyse.yml` |
| 2026-09-25 | **Fixed analyse "Commit fixes" exit-1** on gitignored `ci_temp` (superseded by row above). | run 36099261149 |
| 2026-09-24 | **Implemented HLD-006 snapshot/restore** (self-verifying archive, verify/restore verbs, snapshot endpoints). | HLD-006, BR-37 |
| 2026-09-24 | **`.gitignore` gained review-gate scratch paths** (`ci_temp`, `ci_temp_logs/`). | `.gitignore` |
| 2026-09-23 | **Implemented HLD-005 contextual export** (dossier bundle/preview API, composer skill). | HLD-005, BRD-002 |
| 2026-09-21 | **npm + Claude plugin distribution metadata** and Node launchers. | package distribution |
| 2026-09-21 | **Upstream `main`-tracking realignment** (checkout + four prose spots). | PR #95 review |
| 2026-09-21 | **Review gate synced to upstream OpenCode v2 line** (pin `7cc2d09`; analyse health-check abort). | `ci.md` |
| 2026-09-19 | **Removed root `## Skills` table** (duplicated `.agents/skills/`). | context hygiene |
| 2026-09-20 | **Fixed root changelog LADR count 8→7** and symlink-direction row. | PR #86 review |
| 2026-09-19 | **Added BRD-003 + HLD-007** (understanding kind) and kvasir skill with harness. | BRD-003, HLD-007 |
| 2026-09-17 | **HLD-002 write-pipeline closure** (delegated execution, conflicts, MCP split). | HLD-002 |
| 2026-09-16 | **README temporal-validity wording fix.** | PR #65 review |
| 2026-09-16 | **CI/CD summary: main-only image publication**, no PR artifact uploads. | PR #65 |
| 2026-09-13 | **Added BRD-002 + HLD-005 (discovery)**; ticket/tag traversal recorded Blocked. | `docs/brd/002-contextual-export/` |
| 2026-09-13 | **Documented `scripts/` operational verification** and AGE benchmark command. | `scripts/` |
| 2026-09-13 | **Documented publish-image** tag/concurrency/cache behavior. | `publish-image.yml` |
| 2026-09-14 | **Changelog scoping rule added** (nearest localized file; root for global/root only). | this file |
| 2026-09-14 | **Owned skills renamed to `mimisbrunnr-` prefix**; vitsmunir-dump listed. | PR #55 |
| 2026-09-14 | **Package baseline bump** (Microsoft 10.0.11; Aspire 13.5.3). | this PR |
| 2026-09-15 | **Localized Host integration-test context** added. | PR #63 |
| 2026-09-15 | **Localized operational-scripts context** added; ticket preflight surfaced. | PR #63 |
| 2026-09-15 | **README worked memory example** + atomicity boundary + growth-vs-retrieval section. | `README.md` |
| 2026-09-15 | **Publish-image bullet rewritten** for the coordinated release model. | this PR |
| 2026-09-16 | **README pitch: layered tagging as connective tissue**; session-as-graph. | PR #68 |
| 2026-09-16 | **Added HLD-006 (In Discovery)**, authorized by BR-37. | `docs/hlds/006-corpus-snapshot-and-restore/` |
| 2026-09-16 | **Documented `SMOOTH_FTS_BENCH` recall-tuning harness.** | HLD-001 NFR-02 |
| 2026-09-18 | **Documented `SMOOTH_FEEDBACK_BENCH` placement-evidence harness.** | HLD-004 |
| 2026-09-18 | **Added `mimisbrunnr-muninn-recall-feedback` skill.** | HLD-004 NFR-03 |
| 2026-09-18 | **Review gate moved to local-job packaging** (loopback mTLS terminator). | `pipeline-code-review-report.yml` |
| 2026-09-19 | **PR template `Known Issues` promoted to top-level `## Skip Areas`** (gate greps it). | PR template, `pr-standards` |
| 2026-09-19 | **Review-gate fallbacks realigned to `ANTHROPIC`/`claude-opus-5`** (auto-fix intentionally separate). | `pipeline-code-review-report.yml` |
| 2026-09-19 | **`ci.md` gained fallback-literal, provider-URL, and PR-parsing sections.** | `ci.md` |
| 2026-09-19 | **Corrected two factual errors** in the row above (auto-fix auth path; provider table pin). | PR #83 review |
| 2026-09-19 | **Terminator guard mirrors `model_preset` precedence**; review scratch paths gitignored. | PR #83 review |
| 2026-09-19 | **Recorded gate resolves `OPENCODE-GO-OPENAI`** (vLLM wiring kept as selectable alternative). | run 35444596583 |
| 2026-09-19 | **Gate fallbacks retargeted to `OPENCODE-GO-OPENAI`/`glm-5.2`** (unconfigured consumers land on live provider). | PR #83 review |
| 2026-09-19 | **Added `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md`** (advisory link needs settings enablement). | this PR |
