# AGENTS.md

Guidance for AI coding agents in SmoothAiProductContextMemory.

## Overview

Persistent memory service for AI agents: stores summarized, labelled context (linked to issue/ticket numbers) for retrieval across long gaps. HTTP Docker API + an agent skill for get/set.

**Tech stack:** .NET 10 · ASP.NET Core · Clean Architecture (Domain / Application / Infrastructure / Host) · EF Core + PostgreSQL with Apache AGE · Mediator (source-gen CQRS) · xunit.v3

**Observability:** Serilog is the single logging pipeline (console + Seq sinks) and forwards to the OpenTelemetry logging provider, which exports logs, traces and metrics over OTLP to the Aspire dashboard. Traces cover ASP.NET Core, `HttpClient` and Npgsql, so one request is one trace spanning both stores. Memory content never reaches a log, span attribute or metric tag (NFR-05).

## AI Context Files

`AGENTS.md` and `*AGENTS.md` are first-class AI-coder context — read like `CLAUDE.md`, not optional reference. Layered domain → sub-domain → feature → technology; read every level that governs code you touch, nearest `*AGENTS.md` most authoritative. Keep `*AGENTS.md` synced with code; every PR updates at least one. Prefer local context over adding to this root file — avoid restating the same plan at multiple levels. Changelog entries follow the same rule: record a change in the **nearest localized `*AGENTS.md`'s** changelog, not the root's — the root `## Changelog` is only for changes that are global to the solution or to this root file itself; when no localized `*AGENTS.md` exists for the touched area, the root changelog is that change's home (announcing a newly created doc area counts as solution-global).

## Repository Layout

| Layer | Path | Purpose |
|---|---|---|
| Domain | `src/SmoothAiProductContextMemory.Domain/` | Core entities, value objects — no external deps |
| Application | `src/SmoothAiProductContextMemory.Application/` | Vertical-slice use cases via Mediator — `Features/<Name>/` (contract: `Features/FEATURES_AGENTS.md`), shared code in `Common/` + `Abstractions/` |
| Infrastructure | `src/SmoothAiProductContextMemory.Infrastructure/` | EF Core + PostgreSQL+AGE (`Persistence/`), HTTP clients (`Clients/`), blob storage (`Storage/`) |
| Host | `src/SmoothAiProductContextMemory.Host/` | ASP.NET Core Web API, Serilog → console/Seq + OpenTelemetry OTLP, `/health` + `/alive`, Scalar OpenAPI |
| AppHost | `src/SmoothAiProductContextMemory.AppHost/` | Aspire dev orchestrator — Postgres+AGE (`docker.io/apache/age:release_PG17_1.7.0`) + MinIO blob storage + Seq |
| ChatHost | `src/SmoothAiProductContextMemory.ChatHost/` | Standalone LLM microservice — owns Anthropic SDK; talks to Host via HTTP only (project not yet in tree) |
| Docs | `docs/` | Wiki, HLDs, BRDs — visible (not hidden `.docs`) |
| Scripts | `scripts/` | Operational verification run against a container, not part of the test suite — graph restore round-trip (NFR-03), Postgres pre-upgrade check (NFR-04), a sample-data seeder so both have something to verify, and AppHost teardown (`stop-dev-stack.sh` keep data / `reset-dev-stack.sh` destroy volumes) |

Planned work tracked as worktasks under `.context/work-tasks/` (gitignored). Use `/create worktask`. **Never reference worktask IDs (e.g. `WT-04`) in delivered artefacts** — code, comments, `*AGENTS.md`, HLDs, changelogs. Worktasks are short-lived and gitignored; cite the durable authority instead (HLD, LADR, NFR, PR, issue).

## Skills

| Skill | Path | Purpose |
|---|---|---|
| agile-github-breakdown | `.agents/skills/agile-github-breakdown/` | Braindump/Feature → GitHub Feature + Task graph (Project = initiative, Feature = epic, Task = story). |
| mimisbrunnr-context-memory | `.agents/skills/mimisbrunnr-context-memory/` | Sole interface to the context-memory store; capture (`set`) and retrieval (`get`) of persistent context memories against the HTTP API. |
| mimisbrunnr-vitsmunir-dump | `.agents/skills/mimisbrunnr-vitsmunir-dump/` | Listen-first braindump session for tickets, ADRs, worktasks, requirements or designs; synthesizes only when asked. |
| ai-review | `.agents/skills/ai-review/` | Local consumer of a remote AI code-review report (generator stays remote). |
| git-commit-review-push | `.agents/skills/git-commit-review-push/` | Commit + push + open a PR with an embedded full AI review. |

## Rules

Rules live under `.agents/rules/` as `*.instructions.md`, auto-loaded every session by Claude Code / Cursor / Copilot / Codex (symlinks in `.agents/AI_DEVELOPMENT_AGENTS.md`). Scoping is **per-file** frontmatter: `paths` (Claude), `globs`+`alwaysApply` (Cursor), `applyTo` (Copilot). Category subfolders are organizational only — they don't change loading. One exception: prompt-scoped rules may be deferred for Claude and re-injected on demand by a `UserPromptSubmit` hook (e.g. `code-review-standards`). See `.agents/rules/meta/rules.instructions.md`.

| Category | Folder | Contents |
|----------|--------|----------|
| _(cross-cutting)_ | `.agents/rules/` | `ai-workflow-rules`, `code-review-standards` (hook-deferred), `project-overview`, `skill-secret-handling`, `clean-code`, `solid-principles` |
| git | `.agents/rules/git/` | `git-policy`, `pr-standards` |
| meta | `.agents/rules/meta/` | `rules` (file convention), `knowledge-conventional-contexts-quality` (AGENTS.md quality) |
| backend (`**/*.cs`) | `.agents/rules/backend/` | api-mediator-validation, architecture-slices, backend-logging-conventions, external-api-clients, migrations, readonly-collections, wiremock-stubbing |

## Build / Test

```bash
dotnet build SmoothAiProductContextMemory.slnx                     # build
dotnet test  SmoothAiProductContextMemory.slnx                     # run all tests
dotnet run --project src/SmoothAiProductContextMemory.AppHost      # Aspire: Host from working tree + postgres/blob/seq (group smooth-mímisbrunnr)
HostConfiguration__UseProject=false \
  dotnet run --project src/SmoothAiProductContextMemory.AppHost    # same stack, pull published Host image (tag may lag)
docker build -t smooth-ai-product-context-memory:local .           # Host image (see docs/wiki/docker.md)
dotnet run --project src/SmoothAiProductContextMemory.ChatHost     # ChatHost standalone (separate from API Host)
dotnet run --project src/SmoothAiProductContextMemory.Host -- export [--output DIR] [--history] [--force]
                                                                   # generated Markdown dump of the store (never commit the output)

SMOOTH_AGE_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest \
    --filter Nfr02BenchmarkTests                                   # NFR-02 traversal benchmark (skipped without the env var)
scripts/seed-graph-sample.sh nfr03_sample                          # populate a scratch database with memories + edges
scripts/verify-graph-restore.sh mimisbrunnr-postgres nfr03_sample  # NFR-03 backup/restore round trip incl. edge count
scripts/verify-graph-preupgrade.sh <target-image>                  # NFR-04 pre-upgrade check — run before any Postgres bump
scripts/stop-dev-stack.sh                                          # AppHost teardown: remove mimisbrunnr-{postgres,blob-well,seq,host}, keep volumes
scripts/reset-dev-stack.sh                                         # same, then destroy named volumes (captured corpus gone)
```

Target a single test project (`dotnet test tests/<Project>`) or `ls tests/` to list. **Gotcha:** dev Aspire dashboard at `http://localhost:15278`; first browser visit needs the printed `/login?t=...` URL. **AppHost exit does not stop the stack.** `mimisbrunnr-{postgres,blob-well,seq}` use `ContainerLifetime.Persistent` so captured memories and blobs survive a restart — that is load-bearing, not a leak. `mimisbrunnr-host` is session-lifetime but can remain after a hard kill (`pkill` leaves DCP). Dashboard is in-process and dies with AppHost. Supported teardown: `scripts/stop-dev-stack.sh` (keep data) vs `scripts/reset-dev-stack.sh` (destroy volumes). Do not glob `mimisbrunnr-*` — that also matches `mimisbrunnr-testcontainer-*`. After the AGE image pin, recreate persistent Postgres containers once (`mimisbrunnr-postgres`, `mimisbrunnr-testcontainer-postgres`) — same Persistent mechanism; `stop-dev-stack.sh` is the supported remove for the dev container (see `APPHOST_AGENTS.md`). Same Postgres major (17) as Aspire's old default, so the named data volume is compatible.

## Test Framework

xunit.v3 · Shouldly · Bogus · Respawn. Three tiers (drives where a test belongs):

- **L0** `*.UnitTest` — no I/O, in-process.
- **L1** component — `Application.ComponentTest` (handlers vs real Postgres via Aspire); `Infrastructure.ComponentTest` (real isolated DB).
- **L2** `*.IntegrationTest` — full stack, real PostgreSQL.

Shared fixtures in `tests/SmoothAiProductContextMemory.TestFramework/`; Aspire dependency host (PostgreSQL+AGE + Redis + WireMock + MinIO) in `tests/SmoothAiProductContextMemory.TestFramework.Aspire/`. Both orchestration hosts pin `docker.io/apache/age:release_PG17_1.7.0` — pairing in `docs/hlds/003-graph-edges-on-age/nfrs/NFR-04-version-pairing.md`. See `docs/wiki/testing.md`.

## CI/CD

- **PR gate** — `.github/workflows/pr-gate.yml` (PR→main, push→main, dispatch): restore → build (Release) → Aspire-backed test with coverage via local action `.github/actions/aspire-test-with-coverage`, then publish + upload coverage. Full step list, ports, timings, local tools: `docs/wiki/ci.md`.
- **Publish image** — `.github/workflows/publish-image.yml` builds and pushes the Host image to GHCR for `linux/amd64` and `linux/arm64`; it never runs on pull requests. A push to `main` publishes `:latest` and a short-SHA tag; a SemVer `v*` push publishes the full SemVer tag, its `major.minor` tag, and a short-SHA tag. Manual dispatch accepts only a SemVer-compatible version without a leading `v`, rejects `latest`, and publishes the supplied version plus a short-SHA tag—never `:latest`. Publishing is serialized per Git ref (pushes supersede earlier pushes; dispatches are retained), uses a ref-scoped GitHub Actions build cache, and supplies the commit SHA as the image revision. Run contract: `docs/wiki/docker.md`.
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

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-09-13 | Added BRD-002 (contextual knowledge export, `BR-18`–`BR-34`, extends BRD-001's requirement space) and HLD-005 (discovery). Ticket- and tag-anchored graph traversal recorded as three **Blocked** LADRs — no ticket or tag vertex exists and no writer derives such edges. | `docs/brd/002-contextual-export/`, `docs/hlds/005-contextual-export/` |
| 2026-09-13 | Documented `scripts/` operational verification (NFR-03 restore round-trip, NFR-04 pre-upgrade check) and the `SMOOTH_AGE_BENCH`-gated NFR-02 benchmark command. | `scripts/` |
| 2026-09-13 | Documented publish-image tag derivation, manual-dispatch validation, concurrency, cache, and revision behavior. | `.github/workflows/publish-image.yml` |
| 2026-09-14 | Docs row in the repository layout now lists BRDs alongside HLDs. | `docs/` |
| 2026-09-14 | Changelog scoping rule added to `## AI Context Files`: record changes in the nearest localized `*AGENTS.md`; root changelog is for solution-global or root-file changes only. Localized rows pruned from this table accordingly. | this file |
| 2026-09-14 | Skills table corrected after the owned-skill rename to the `mimisbrunnr-` prefix: `context-memory` → `mimisbrunnr-context-memory`, and the previously unlisted `mimisbrunnr-vitsmunir-dump` added. | PR #55 |
