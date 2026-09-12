# AGENTS.md

Guidance for AI coding agents in SmoothAiProductContextMemory.

## Overview

Persistent memory service for AI agents: stores summarized, labelled context (linked to issue/ticket numbers) for retrieval across long gaps. HTTP Docker API + an agent skill for get/set.

**Tech stack:** .NET 10 · ASP.NET Core · Clean Architecture (Domain / Application / Infrastructure / Host) · EF Core + PostgreSQL · Mediator (source-gen CQRS) · xunit.v3

## AI Context Files

`AGENTS.md` and `*AGENTS.md` are first-class AI-coder context — read like `CLAUDE.md`, not optional reference. Layered domain → sub-domain → feature → technology; read every level that governs code you touch, nearest `*AGENTS.md` most authoritative. Keep `*AGENTS.md` synced with code; every PR updates at least one. Prefer local context over adding to this root file — avoid restating the same plan at multiple levels.

## Repository Layout

| Layer | Path | Purpose |
|---|---|---|
| Domain | `src/SmoothAiProductContextMemory.Domain/` | Core entities, value objects — no external deps |
| Application | `src/SmoothAiProductContextMemory.Application/` | Vertical-slice use cases via Mediator — `Features/<Name>/` (contract: `Features/FEATURES_AGENTS.md`), shared code in `Common/` + `Abstractions/` |
| Infrastructure | `src/SmoothAiProductContextMemory.Infrastructure/` | EF Core + PostgreSQL (`Persistence/`), HTTP clients (`Clients/`), blob storage (`Storage/`) |
| Host | `src/SmoothAiProductContextMemory.Host/` | ASP.NET Core Web API, Serilog, Scalar OpenAPI |
| AppHost | `src/SmoothAiProductContextMemory.AppHost/` | Aspire dev orchestrator — Postgres + MinIO blob storage + Seq |
| ChatHost | `src/SmoothAiProductContextMemory.ChatHost/` | Standalone LLM microservice — owns Anthropic SDK; talks to Host via HTTP only (project not yet in tree) |

Planned work tracked as worktasks under `.context/work-tasks/` (gitignored). Use `/create worktask`.

## Skills

| Skill | Path | Purpose |
|---|---|---|
| context-memory | `.agents/skills/context-memory/` | Sole interface to the context-memory store; capture (`set`) and retrieval (`get`) of persistent context memories against the HTTP API. |
| ai-review | `.agents/skills/ai-review/` | Local consumer of a remote AI code-review report (generator stays remote). |
| git-commit-review-push | `.agents/skills/git-commit-review-push/` | Commit + push + open a PR with an embedded full AI review. |

## Rules

Rules live under `.agents/rules/` as `*.instructions.md`, auto-loaded every session by Claude Code / Cursor / Copilot / Codex (symlinks in `.agents/AI_DEVELOPMENT_AGENTS.md`). Scoping is **per-file** frontmatter: `paths` (Claude), `globs`+`alwaysApply` (Cursor), `applyTo` (Copilot). Category subfolders are organizational only — they don't change loading. One exception: prompt-scoped rules may be deferred for Claude and re-injected on demand by a `UserPromptSubmit` hook (e.g. `code-review-standards`). See `.agents/rules/meta/rules.instructions.md`.

| Category | Folder | Contents |
|----------|--------|----------|
| _(cross-cutting)_ | `.agents/rules/` | `ai-workflow-rules`, `code-review-standards` (hook-deferred), `project-overview`, `skill-secret-handling` |
| git | `.agents/rules/git/` | `git-policy`, `pr-standards` |
| meta | `.agents/rules/meta/` | `rules` (file convention), `knowledge-conventional-contexts-quality` (AGENTS.md quality) |
| backend (`**/*.cs`) | `.agents/rules/backend/` | api-mediator-validation, architecture-slices, backend-logging, external-api-clients, migrations, wiremock-stubbing |

## Build / Test

```bash
dotnet build SmoothAiProductContextMemory.slnx                     # build
dotnet test  SmoothAiProductContextMemory.slnx                     # run all tests
dotnet run --project src/SmoothAiProductContextMemory.AppHost      # dev Aspire AppHost
dotnet run --project src/SmoothAiProductContextMemory.ChatHost     # ChatHost standalone (separate from API Host)
```

Target a single test project (`dotnet test tests/<Project>`) or `ls tests/` to list. **Gotcha:** dev Aspire dashboard at `http://localhost:15278`; first browser visit needs the printed `/login?t=...` URL.

## Test Framework

xunit.v3 · Shouldly · Bogus · Respawn. Three tiers (drives where a test belongs):

- **L0** `*.UnitTest` — no I/O, in-process.
- **L1** component — `Application.ComponentTest` (handlers vs real Postgres via Aspire); `Infrastructure.ComponentTest` (real isolated DB).
- **L2** `*.IntegrationTest` — full stack, real PostgreSQL.

Shared fixtures in `tests/SmoothAiProductContextMemory.TestFramework/`; Aspire dependency host (PostgreSQL + WireMock) in `tests/SmoothAiProductContextMemory.TestFramework.Aspire/`. See `.docs/wiki/testing.md`.

## CI/CD

- **PR gate** — `.github/workflows/pr-gate.yml` (PR→main, push→main, dispatch): restore → build (Release) → Aspire-backed test with coverage via local action `.github/actions/aspire-test-with-coverage`, then publish + upload coverage. Full step list, ports, timings, local tools: `.docs/wiki/ci.md`.
- **AI PR review** — `.github/workflows/pipeline-code-review-report.yml` is a thin caller for the `smooth-ai-report-review` reusable workflow; posts an OpenCode review report on PRs (opened/synchronize/reopened/ready_for_review, `/ai-review` comment, dispatch). `.github/workflows/pipeline-ai-analyse.yml` runs after it, auto-fixes 🟡 Medium / 🔵 Low findings (bounded by `OPENCODE_ANALYSE_MAX_INCREMENTAL`). Both need org-level `OPENCODE_*` secrets/variables (provider OpenAI). Local-only consumer skill at `.agents/skills/ai-review`; report *generator* stays remote. To commit+push a branch so the pushed PR gets a **full** review, use `/git-commit-review-push` (`.agents/skills/git-commit-review-push`) — embeds `/ai-review` in the last commit.

## Git Constraints

Hosted on **GitHub** at `https://github.com/generic-automation-and-it/project`. Use `gh` for PR/repo ops. PR template: `.github/pull_request_template.md`. Code owners: `.github/CODEOWNERS` — all files by `@generic-automation-and-it/project`. Commit/PR formats and branch naming: `.agents/rules/git/`.

## Glossary

| Term | Description |
|---|---|
| Context | Summarized, labelled unit of knowledge stored for later retrieval |
| Label | Tag (e.g. issue/ticket number) linking/retrieving related contexts |
| Semantic memory | Embedded, labelled knowledge — primary store for retrieval by label or similarity |
| Episodic memory | Timestamped record of when/where a context was captured |
| Procedural memory | Persisted agent/user preferences and learned behaviors |
