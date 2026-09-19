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
| Scripts | `scripts/` | Operational verification run against a container, not part of the test suite — graph restore round-trip (NFR-03), Postgres pre-upgrade check (NFR-04), ticket-ownership preflight before the ticket-graph migration (`check-ticket-ownership.sh`), a sample-data seeder, and AppHost teardown (`stop-dev-stack.sh` keep data / `reset-dev-stack.sh` destroy volumes). Local contract: [scripts/AGENTS.md](scripts/AGENTS.md). |

Planned work tracked as worktasks under `.context/work-tasks/` (gitignored). Use `/create worktask`. **Never reference worktask IDs (e.g. `WT-04`) in delivered artefacts** — code, comments, `*AGENTS.md`, HLDs, changelogs. Worktasks are short-lived and gitignored; cite the durable authority instead (HLD, LADR, NFR, PR, issue).

## Skills

| Skill | Path | Purpose |
|---|---|---|
| agile-github-breakdown | `.agents/skills/agile-github-breakdown/` | Braindump/Feature → GitHub Feature + Task graph (Project = initiative, Feature = epic, Task = story). |
| mimisbrunnr-context-memory | `.agents/skills/mimisbrunnr-context-memory/` | Sole interface to the context-memory store; capture (`set`) and retrieval (`get`) of persistent context memories against the HTTP API. |
| mimisbrunnr-vitsmunir-dump | `.agents/skills/mimisbrunnr-vitsmunir-dump/` | Listen-first braindump session for tickets, ADRs, worktasks, requirements or designs; synthesizes only when asked. |
| mimisbrunnr-recall-feedback | `.agents/skills/mimisbrunnr-recall-feedback/` | Run the three recall-feedback tuning queries against the Host API — never-recalled list, miss rate, baseline reset (HLD-004 NFR-03). |
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
SMOOTH_FTS_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest \
    --filter RecallTuningEvidenceTests                             # recall-tuning evidence harness (skipped without the env var)
SMOOTH_FEEDBACK_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest \
    --filter FeedbackPlacementEvidenceTests                        # HLD-004 feedback-placement evidence (skipped without the env var)
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

- **PR gate** — `.github/workflows/pr-gate.yml` (PR→main, push→main, dispatch): restore → build (Release) → Aspire-backed tests and coverage summary, plus native container builds. PR/manual CI uploads no build-record or coverage artifacts; main pushes may upload them. Full contract: `docs/wiki/ci.md`.
- **Publish image** — `.github/workflows/publish-image.yml` publishes API and `-apphost` controller images only on main pushes after merge, not PRs, tag pushes, or manual dispatch. One repository-wide `queue: max` group serializes same-commit tests, multi-platform candidates, native smoke, and `latest`/SHA promotion. No GitHub Release/tag creation path. Require PR-only main updates through branch protection; see `docs/wiki/ci.md`.
- **AI PR review** — `.github/workflows/pipeline-code-review-report.yml` runs the review gate as a **local job**, not a reusable-workflow caller: it checks out `generic-automation-and-it/smooth-ai-report-review` at a pinned SHA into `.review-tools/` and invokes that repo's `run-review.sh`. The packaging changed because the review provider is a private vLLM gateway requiring mTLS, which opencode's fetch-based SDKs cannot do — the job must first start `.github/scripts/vllm-tls-proxy.py` as a loopback terminator, and a reusable-workflow caller cannot inject a step into the callee's job. That `ANTHROPIC`-plus-terminator wiring is configured but **not currently selected** — gate runs resolve `OPENCODE-GO-OPENAI` and skip the terminator step (verified run 35444596583, 2026-09-19). When it *is* selected, the provider is `ANTHROPIC` retargeted to `http://127.0.0.1:8888/v1` through `.github/opencode.json` (`OPENCODE_REVIEW_REPORT_CONFIG`); that gateway serves Anthropic-named model aliases, and the upstream `anthropic` block is never baseURL-injected, so the literal survives. Read the `🔀 OpenCode provider:` line in a run log for the live value — Actions Variables are not readable through the integration token. The workflow **name** `PR Code Review Report` is a contract with `pipeline-ai-analyse.yml`'s `workflow_run.workflows` — rename both or neither. `pipeline-ai-analyse.yml` runs after it and auto-fixes 🟡 Medium / 🔵 Low findings (bounded by `OPENCODE_ANALYSE_MAX_INCREMENTAL`); keeping auto-fix on an OpenCode Go provider requires setting **both** `OPENCODE_ANALYSE_PROVIDER` and `OPENCODE_ANALYSE_MODEL`, because `OPENCODE_ANALYSE_MODEL` otherwise inherits `OPENCODE_REVIEW_REPORT_MODEL_PRIMARY` and the analyse scope then fails resolution. Local-only consumer skill at `.agents/skills/ai-review`; report *generator* stays remote. To commit+push a branch so the pushed PR gets a **full** review, use `/git-commit-review-push` (`.agents/skills/git-commit-review-push`) — embeds `/ai-review` in the last commit. Secrets, variables and the proxy contract: `docs/wiki/ci.md`.

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
| 2026-09-17 | HLD-002 write-pipeline closure delivered: delegated read/write execution, conflict mechanics, and MCP capability split -- detail in the skill and AI-development changelogs. | HLD-002 |
| 2026-09-16 | Corrected README temporal-validity wording: retrieval filters validity windows only when `asOf` is supplied. | PR #65 review |
| 2026-09-16 | CI/CD summary now states main-only image publication and no PR/manual build-record or coverage artifact uploads. | PR #65 |
| 2026-09-13 | Added BRD-002 (contextual knowledge export, `BR-18`–`BR-34`, extends BRD-001's requirement space) and HLD-005 (discovery). Ticket- and tag-anchored graph traversal recorded as three **Blocked** LADRs — no ticket or tag vertex exists and no writer derives such edges. | `docs/brd/002-contextual-export/`, `docs/hlds/005-contextual-export/` |
| 2026-09-13 | Documented `scripts/` operational verification (NFR-03 restore round-trip, NFR-04 pre-upgrade check) and the `SMOOTH_AGE_BENCH`-gated NFR-02 benchmark command. | `scripts/` |
| 2026-09-13 | Documented publish-image tag derivation, manual-dispatch validation, concurrency, cache, and revision behavior. | `.github/workflows/publish-image.yml` |
| 2026-09-14 | Docs row in the repository layout now lists BRDs alongside HLDs. | `docs/` |
| 2026-09-14 | Changelog scoping rule added to `## AI Context Files`: record changes in the nearest localized `*AGENTS.md`; root changelog is for solution-global or root-file changes only. Localized rows pruned from this table accordingly. | this file |
| 2026-09-14 | Skills table corrected after the owned-skill rename to the `mimisbrunnr-` prefix: `context-memory` → `mimisbrunnr-context-memory`, and the previously unlisted `mimisbrunnr-vitsmunir-dump` added. | PR #55 |
| 2026-09-14 | Microsoft package baseline 10.0.7/10.0.8 -> 10.0.11 (EF Core, Extensions, AspNetCore.OpenApi, Mvc.Testing, EF InMemory); Aspire 13.3.0 -> 13.5.3 (AppHost-localized row in APPHOST_AGENTS.md). | this PR |
| 2026-09-15 | Added localized Host integration-test context for exact-request telemetry assertions and ticket graph L2 coverage. | [INTEGRATION_TEST_AGENTS.md](tests/SmoothAiProductContextMemory.Host.IntegrationTest/INTEGRATION_TEST_AGENTS.md), PR #63 |
| 2026-09-15 | Added localized operational-scripts context and surfaced ticket-ownership preflight in the repository layout. | [scripts/AGENTS.md](scripts/AGENTS.md), [ticket migration runbook](docs/hlds/003-graph-edges-on-age/ticket-migration-runbook.md), PR #63 |
| 2026-09-15 | README gained a worked memory example, memory-boundary (atomicity, no mechanical chunking) explanation, and a "store grows but retrieved context doesn't" section — placed above the memory model for novice readers. | `README.md` |
| 2026-09-15 | Publish-image CI/CD bullet rewritten for the coordinated release model: two images (API + `-apphost` controller), one repository-wide `queue: max` promotion group, prereleases never update stable aliases. | this PR |
| 2026-09-16 | README opening pitch now names layered tagging as the connective tissue (anchors + edges, session as referenced graph); plus "A session lands as a graph, not a transcript" — the classification levels (initiative, scope, repo, ticket, subject, tags/facets, claim labels), which two are AGE edges (memory `LINKS`, ticket hierarchy), and the deliberate absence of a tag graph. | `README.md`, PR #68 |
| 2026-09-16 | Added HLD-006 (corpus snapshot and restore, In Discovery) — durability of the store as one asset: a self-verifying archive over both stores plus a verified restore path. Authorized by BRD-001's third amendment (`BR-37`), which closed the durability gap in that BRD's requirement space. | `docs/hlds/006-corpus-snapshot-and-restore/`, `docs/brd/001-context-memory/` |
| 2026-09-16 | Documented the `SMOOTH_FTS_BENCH`-gated recall-tuning evidence harness in the Build/Test catalogue beside its `SMOOTH_AGE_BENCH` sibling. | HLD-001 NFR-02 recall-tuning measurements |
| 2026-09-18 | Documented the `SMOOTH_FEEDBACK_BENCH`-gated feedback-placement evidence harness in the Build/Test catalogue, beside its two `*_BENCH` siblings. | HLD-004 LADR-02 placement evidence |
| 2026-09-18 | Added the `mimisbrunnr-recall-feedback` skill (never-recalled / miss-rate / reset against the Host API) to the Skills table. | HLD-004 NFR-03 |
| 2026-09-18 | AI PR review moved from the reusable-workflow caller to upstream's local-job packaging so the job can start a loopback mTLS terminator for a private vLLM gateway; provider retargeted to `ANTHROPIC` via a repo-local `opencode.json`. Auto-fix now needs explicit `OPENCODE_ANALYSE_*` values to stay on its Go provider. | `.github/workflows/pipeline-code-review-report.yml`, [ci.md](docs/wiki/ci.md) |
| 2026-09-19 | PR template's `**Known Issues:**` promoted out of `## AI Review Notes` into a sibling `## Skip Areas / Known Issues` section. The gate's `extract-review-notes.sh` greps two top-level headings, so the nested form reached no prompt and every intentional skip was re-raised next round. Verified by round-trip, not by eye. | `.github/pull_request_template.md`, [pr-standards](.agents/rules/git/pr-standards.instructions.md) |
| 2026-09-19 | Review-gate unset-Variable fallbacks realigned from Gemini to the configured `ANTHROPIC`/`claude-opus-5` standard (selector, terminator guard, provider-id ladder, three model tiers). `GEMINI` gained an explicit provider-id row, since it was previously reachable only as the ladder's bare final literal. Auto-fix keeps its separate `OPENAI` fallbacks on purpose — that job starts no terminator. | `.github/workflows/pipeline-code-review-report.yml`, [ci.md](docs/wiki/ci.md) |
| 2026-09-19 | `ci.md` gained three sections: where the fallback literals live and why the two workflows disagree, the fixed-base vs variable-base provider-URL split (no fallback exists for `GEMINI`/`COPILOT`/`OPENAI`), and what the gate parses out of a PR description. | [ci.md](docs/wiki/ci.md) |
| 2026-09-19 | Corrected two factual errors in the row above, both found in review. The reason auto-fix must not move to `ANTHROPIC` is that its job sets no `OPENCODE_REVIEW_REPORT_CONFIG` and so loads upstream's `assets/opencode.json` — it fails on auth against the public API, not on the gate's loopback socket, which is never in play there. And the provider table must be read at the pinned SHA: `OPENCODE-GO-RESPONSES` exists only on upstream `main`, which the gate does not run. | PR #83 review, [ci.md](docs/wiki/ci.md) |
| 2026-09-19 | Terminator guard now mirrors the provider selector's `model_preset` precedence instead of keying on the Variable alone — a dispatch with an Anthropic preset could select the gateway while the guard skipped the step. `.review-tools/` and `.smooth-ai-review-tools/` gitignored, and the documented verification commands fetch the lib at the pin rather than assuming a runner-only checkout. | PR #83 review |
| 2026-09-19 | Recorded that the review gate resolves `OPENCODE-GO-OPENAI`, not the `ANTHROPIC`/vLLM wiring this file and `ci.md` described as current — the terminator step is skipped on every run. The vLLM documentation is kept and relabelled as the selectable alternative rather than deleted. Whether the unset-Variable fallbacks should follow to `OPENCODE-GO-OPENAI` is left open, not silently changed. | PR #83 review, run 35444596583 |
| 2026-09-19 | Added the three community health files the repo was missing — `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md` — and linked them from the README's Contributing section. Deliberately terse: they point at `.agents/rules/git/` and `docs/wiki/ci.md` rather than restating them. **`SECURITY.md`'s advisory link 404s until Private Vulnerability Reporting is enabled in repository settings**; it is currently `false` and cannot be set from an integration token. | this PR |
