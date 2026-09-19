# CI/CD

Three workflow families: the PR gate (build + test), the AI PR review gate plus its
auto-fix sibling, and a separate image publish to GHCR.
The publish workflow does **not** run on pull requests.

## PR Gate

- **Workflow:** `.github/workflows/pr-gate.yml`
- **Triggers:** `pull_request` → `main` (including PR branch updates), `push` → `main`, and manual `workflow_dispatch`.
- **Paths filter:** source/tests, scripts, the owned context-memory skill and its two project-agent registrations, the `.mcp.json` MCP server config, Dockerfiles, solution/build/package inputs, local actions, and PR/publish workflows trigger checks. Docs-only PRs skip the gate; dispatch and reusable workflow calls run explicitly.
- **Policy checks:** main-only release event/promotion tests and engine-free controller preflight/lifecycle tests run with the build. PRs also build both container architectures on native runners without publication. PR/manual CI keeps logs, summaries, and caches, but uploads neither Docker build records nor coverage artifacts. Images are build-only; full packaged-controller smoke runs in the main publication pipeline.

### Steps

1. **Checkout** — `actions/checkout@v4`.
2. **Test release policy** - `python3 -B -m unittest discover -s scripts -p 'test_release_policy.py' -v`; fails closed before installing the SDK.
3. **Test context-memory skill** - two scripts in one step: `python3 -B .agents/skills/mimisbrunnr-context-memory/tests/run_tests.py` (deterministic plumbing, agent grants, deep-search bounds, divergence composition) and `python3 -B .agents/skills/mimisbrunnr-context-memory/tests/measure_cost.py` (reproducible structural cost evidence; never emits memory content).
4. **Install .NET SDK** — `actions/setup-dotnet@v4` (version from the `DOTNET_VERSION` env, currently `10.0.x`).
5. **Restore** — `dotnet restore`.
6. **Build** — `dotnet build --no-restore --configuration Release`.
7. **Test controller preflight and lifecycle** - `python3 scripts/test-apphost-entrypoint.py`; engine-free tests against the built AppHost output.
8. **Aspire test with coverage** — local action `.github/actions/aspire-test-with-coverage`:
    - Starts `tests/SmoothAiProductContextMemory.TestFramework.Aspire`, keeps its PID inside the action script, and waits for PostgreSQL (`127.0.0.1:15432`, image `docker.io/apache/age:release_PG17_1.7.0`), Redis (`127.0.0.1:16379`), WireMock (`http://127.0.0.1:19091/__admin/health`), MinIO TCP (`127.0.0.1:9002`), then MinIO HTTP (`http://127.0.0.1:9002/minio/health/live`). On MinIO timeout the action dumps `docker logs mimisbrunnr-testcontainer-blob`. MinIO image is pinned to `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z`.
   - Restores .NET tools (`dotnet tool restore`) after the dependency pre-warm, matching the proven CI timing before tests start.
   - Prepares `artifacts/testresults/` and `artifacts/coverage/`.
    - Runs test projects in order: Host integration → Application/Infrastructure component → Domain/Application/Infrastructure/Host/AppHost unit tests.
   - Generates coverage reports with `dotnet tool run reportgenerator`.
   - Stops the Aspire host from the action script's teardown trap once tests and coverage have finished or failed.
9. **Publish coverage summary** (`if: always()`) — appends `artifacts/coverage/SummaryGithub.md` to the GitHub step summary.
10. **Upload coverage artifacts** — uploads `artifacts/coverage/` as `coverage-report` only for main pushes (including failed main runs). PR and manual CI skip upload.

## .NET local tools

`.config/dotnet-tools.json` declares the local tool manifest, restored in CI (and locally) with `dotnet tool restore`:

| Tool | Version | Command |
|---|---|---|
| `dotnet-reportgenerator-globaltool` | `5.4.4` | `reportgenerator` |
| `dotnet-ef` | `10.0.8` | `dotnet-ef` |

`dotnet-ef` is pinned to the EF Core runtime version (`Directory.Packages.props`) so the migrations CLI never drifts from the `Microsoft.EntityFrameworkCore.*` packages. Bump both together.

## AI PR review

- **Workflows:** `.github/workflows/pipeline-code-review-report.yml` (the gate) and
  `.github/workflows/pipeline-ai-analyse.yml` (auto-fix, triggered by the gate's `workflow_run`).
- **Triggers:** `pull_request` (opened/synchronize/reopened/ready_for_review), an `/ai-review` comment from an
  OWNER/MEMBER/COLLABORATOR, and `workflow_dispatch`.
- **Packaging:** local job, **not** a reusable-workflow call. The job checks out
  `generic-automation-and-it/smooth-ai-report-review` at a pinned SHA into `.review-tools/` and invokes that repo's
  `.agents/skills/ai-review-report/scripts/run-review.sh`. Bump the pinned `ref:` deliberately; a SHA predating that
  entrypoint fails the gate with "No such file or directory".
- **Why local-job:** the review provider is a private vLLM gateway that requires a client certificate and a private
  CA. opencode's provider SDKs use Node/Bun `fetch`, which supports neither, so the job must first start a loopback
  terminator. A reusable-workflow caller cannot inject a step into the callee's job, and a separate job would get a
  separate runner — so the steps have to live here.

### Which provider actually runs

**The vLLM/`ANTHROPIC` wiring described below is configured but not currently selected.** Verified from run
[35444596583](https://github.com/generic-automation-and-it/smooth-ai-product-context-memory/actions/runs/35444596583)
(2026-09-19, commit `5302954`):

```
🔀 OpenCode provider: OPENCODE-GO-OPENAI (provider-id: go-openai)
OPENCODE_REVIEW_REPORT_GATEWAY_URL: https://opencode.ai/zen/go/v1
Start vLLM mTLS terminator -> skipped
```

So `OPENCODE_REVIEW_REPORT_PROVIDER` is set to `OPENCODE-GO-OPENAI`, the terminator step is skipped on every run,
and the private gateway is not in the request path. Read the rest of this section as **the alternative
configuration**, kept because it is selectable by flipping that one Variable — not as a description of today's runs.

Check which is live by reading the `🔀 OpenCode provider:` line in any gate run's log. Actions Variables are not
readable through the repo's integration token (`gh api .../actions/variables` → 403), so the log is the available
source of truth.

The gate's unset-Variable fallbacks follow this live provider: `OPENCODE-GO-OPENAI` with `glm-5.2` on all three
tiers, so an unconfigured consumer lands where a configured one already is. The live Variables at the run above were
`glm-5.2` / `grok-4.6` / `muse-spark-1.3-contributor`; the fallback pins only `glm-5.2`, which is both the live
primary and a model `.github/opencode.json` declares.

### Provider wiring (the ANTHROPIC/vLLM alternative)

| Piece | Value |
|---|---|
| Terminator | `.github/scripts/vllm-tls-proxy.py`, plain HTTP on `127.0.0.1:8888`, mTLS out to the gateway in the profile |
| opencode config | `.github/opencode.json`, selected via the `OPENCODE_REVIEW_REPORT_CONFIG` variable |
| Retarget | `provider.anthropic.options.baseURL` = `http://127.0.0.1:8888/v1` as a **literal** |
| Provider selector | `OPENCODE_REVIEW_REPORT_PROVIDER=ANTHROPIC` — **not the current value**, see above |

Two upstream behaviours make that work and must not be "corrected":

- The gate's `prepare-opencode-config.sh` injects `baseURL` only for `gemini`, `github-copilot` and `openai`. The
  `anthropic` block is deliberately never injected, so a literal written here survives into the resolved config.
- `resolve-provider.sh` requires every `OPENCODE_REVIEW_REPORT_MODEL_*` value to start with `claude` when the
  provider is `ANTHROPIC`. The gateway serves Anthropic-named aliases, so that gate is satisfied. Selecting
  `OPENCODE-GO-ANTHROPIC` instead fails: its family check rejects both `claude*` and `deepseek*`, which is
  everything this gateway serves.

The `/v1` suffix on the baseURL is required — the SDK appends only `/messages`, and the gateway answers `404` on a
bare `/messages`. `small_model` is pinned to the one declared model so opencode's title/summary heuristic cannot
select an id the gateway lacks.

The config wires `anthropic` (the review provider), `go-openai` and `go-anthropic` (**keep these — auto-fix runs on
a Go provider and reads this same config**), plus `openai` and `openrouter`. Two measured caveats about what this
file does and does not control:

- **The `models` block does not restrict model choice.** opencode merges its own catalogue over it, so with a single
  `claude-opus-5` entry declared, `opencode models` still resolves 15 `anthropic/*` ids — including ones this gateway
  `404`s. Only the `OPENCODE_REVIEW_REPORT_MODEL_*` variables decide what actually runs.
- **Deleting a provider block does not remove the provider.** `github-copilot` still appears in `opencode models`
  after its block is deleted, because opencode discovers it independently. What deletion removes is our credential
  and baseURL wiring — so pointing `OPENCODE_REVIEW_REPORT_PROVIDER` at a deleted provider passes
  `resolve-provider.sh` (which never reads this file) and then fails at request time with an auth error rather than a
  clear configuration error.

### Required configuration

| Kind | Name | Value |
|---|---|---|
| Secret | `OPENCODE_VLLM_PROFILE_B64` | `base64` of the vllm-proxy `profile.json` (single line) |
| Secret | `OPENCODE_ANTHROPIC_API_KEY` | any non-empty placeholder — the terminator supplies the real credential |
| Variable | `OPENCODE_REVIEW_REPORT_PROVIDER` | `ANTHROPIC` **for this wiring only**; currently `OPENCODE-GO-OPENAI`, which needs none of the rows in this table |
| Variable | `OPENCODE_REVIEW_REPORT_CONFIG` | `.github/opencode.json` |
| Variable | `OPENCODE_REVIEW_REPORT_MODEL_PRIMARY` / `_SECONDARY` / `_ORCHESTRATOR` | a `claude-*` alias the gateway serves |
| Variable | `OPENCODE_REVIEW_REPORT_MAX_PARALLEL` | `2` — one vLLM instance backs every chunk; the upstream default of 7 pushes chunks past their budget |
| Variable | `OPENCODE_ANALYSE_PROVIDER` + `OPENCODE_ANALYSE_MODEL` | **both** required to keep auto-fix off the vLLM gateway (see below) |

`OPENCODE_ANALYSE_MODEL` falls back to `OPENCODE_REVIEW_REPORT_MODEL_PRIMARY`. With the review tiers on a `claude-*`
alias and `OPENCODE_ANALYSE_PROVIDER` unset, the analyse scope aborts with *"OPENCODE_ANALYSE_MODEL is set but
OPENCODE_ANALYSE_PROVIDER is unset"*. Set both, or auto-fix stops running. Note also that the analyse job neither starts the terminator nor sets
`OPENCODE_REVIEW_REPORT_CONFIG`, so its fallback chain — which still resolves to the review provider — reaches the
**public** `api.anthropic.com` carrying the placeholder key and fails on auth; only its primary target is live.

### Fallback literals when the Variables are unset

Every **Variable** row in the table above has a hardcoded fallback in the workflow YAML for the run where it is not
set. The two `Secret` rows do not, and must not — a secret with a committed default is the shape
[`skill-secret-handling`](../../.agents/rules/skill-secret-handling.instructions.md) forbids; the gate forwards
both bare and fails loudly when they are empty. The fallbacks are duplicated per workflow rather than shared, and the two workflows
**deliberately disagree**:

| Workflow | Provider fallback | Model fallbacks | Why |
|---|---|---|---|
| `pipeline-code-review-report.yml` | `OPENCODE-GO-OPENAI`, provider-id `go-openai` | `glm-5.2` on all three tiers | Matches the provider the gate actually resolves; needs no terminator and no URL Variable |
| `pipeline-ai-analyse.yml` | `OPENAI` | `gpt-5.5` / `gpt-5.4` / `gpt-5.4-mini` | Auto-fix must stay **off** the review credential — this job starts no terminator and loads a different opencode config |

Do not "align" the analyse fallbacks onto the gate's. The mechanism is not the one you might assume: the analyse job
never sets `OPENCODE_REVIEW_REPORT_CONFIG`, so `prepare-opencode-config.sh` falls back to **upstream's** committed
`assets/opencode.json` rather than this repo's `.github/opencode.json`. That asset pins the **public**
`https://api.anthropic.com` as the `anthropic` provider's `baseURL` (verified at pin `4bdfea4`), so pointing
auto-fix at `ANTHROPIC` sends the placeholder `OPENCODE_ANTHROPIC_API_KEY` to the real Anthropic API and fails on
**auth**, not on a dead loopback socket. The gate's `http://127.0.0.1:8888/v1` literal is never in play there at
all — it exists only in this repo's config, which that job does not load.

Two couplings make a partial edit silent rather than loud:

- **Provider and models move together.** `resolve-provider.sh` applies a per-provider family check — under
  `OPENCODE-GO-OPENAI` it rejects `claude*`, `gemini*` and `minimax*`/`qwen*`; under `ANTHROPIC` it rejects anything
  that is not `claude*`. Changing the provider fallback without the three model fallbacks
  aborts at preflight, far from the line that was missed.
- **The provider-id chain has a bare final literal.** In the gate, `OPENCODE_REVIEW_REPORT_PROVIDER_ID` is a long
  `||` ladder whose last line is an unguarded provider id. Repointing that literal silently changes the id for any
  provider that had no explicit row of its own — `GEMINI` now carries one for exactly that reason.

Re-grep after any such change and expect no stray hits:

```bash
grep -rn "|| 'GEMINI'\|'gemini-\|:-GEMINI}" --include='*.yml' --include='*.sh' \
  --exclude-dir=.review-tools --exclude-dir=.smooth-ai-review-tools .
```

The excludes matter: a leftover tooling checkout contains upstream's own `:-GEMINI}` default and would report a hit
that is not yours.

### Provider base URLs are not symmetric

`resolve-provider.sh` splits providers into two shapes, and only one of them works from a key alone:

| Shape | Providers | What is needed |
|---|---|---|
| Fixed base | `ANTHROPIC`, `OPENCODE-GO-OPENAI`, `OPENCODE-GO-ANTHROPIC`, `OPEN_ROUTER` | the API key Secret only |
| Variable base | `GEMINI`, `COPILOT`, `OPENAI` | the key **and** an `OPENCODE_REVIEW_REPORT_<P>_URL` Variable |

**Read that table against the pin, not against upstream `main`.** It lists what `_rp_provider_fields` accepts at
the SHA the gate checks out (`4bdfea4`). Upstream `main` has since added `OPENCODE-GO-RESPONSES`, which this pin
rejects as an unknown provider — and which has no row in the gate's provider-id ladder, so bumping the pin without
adding one would silently map it to `anthropic`. The two workflows do not even agree on the ref: the gate pins a
SHA, while `pipeline-ai-analyse.yml` tracks `main` (overridable via `SMOOTH_AI_REVIEW_TOOLS_REF`). Re-read the
function at whichever ref you are changing.

There is no fallback URL for the variable-base three — `_rp_resolve` hard-fails on an empty value. That is why the
gate's unset-Variable fallback is `OPENCODE-GO-OPENAI` (fixed base) and not a variable-base provider: the old
`GEMINI` fallback made an unconfigured run die on `OPENCODE_REVIEW_REPORT_GEMINI_URL`, naming a provider nobody had
selected.

Do not add a hardcoded fourth base URL to make a variable-base provider work out of the box. The `OPENAI` slot
exists as the relay point for a proxy; a literal `https://api.openai.com/v1` would send a proxy key to OpenAI.

### What the gate reads from the PR description

`lib/extract-review-notes.sh` pulls exactly two **top-level** headings out of the PR body and feeds them to the
review prompt: `^## AI Review Notes` and `^## Skip Areas`. Each section walk stops at the next `^## `.

The Skip Areas bullets are the channel that tells the next round which findings are intentional. A
`**Known Issues:**` line nested inside `## AI Review Notes` is not a heading at all, so it never reaches the prompt
and every skip is re-raised. `.github/pull_request_template.md` therefore ships the two as siblings, and
`ai-review` writes skips into the Skip Areas section rather than the summary table it also appends.

#### A lone HTML comment truncates everything below it

`_clean()` in that lib strips comments with `sed '/^<!--/,/-->$/d'`. A sed range whose start and end
match on the **same line** does not close there — sed looks for the end pattern from the *next* line on. A
self-closing one-line comment therefore opens a range that never closes, and everything from it to the end of the
body is deleted.

This is not hypothetical here: Conductor appends `<!-- conductor-workspace-link -->` to PR descriptions it creates.
Verified on PR #83 — the `### AI Review Response` block placed below that marker reached the prompt as zero lines,
while the same block moved above it extracted fine.

Consequence, and the rule: **keep `## Skip Areas / Known Issues` above any one-line HTML comment in the body.** It
currently sits above the Conductor marker by ordering luck, not design. Placed below — which `ai-review`'s own
"append at the end" fallback would do — every skip bullet would be silently truncated, reproducing the LADR-083
failure this section exists to prevent. The round-trip check below catches it; run it after any body edit.

```bash
printf 'keep A\n<!-- marker -->\nkeep B\n' | sed '/^<!--/,/-->$/d'   # prints only "keep A"
```

Verify a PR body by round-trip rather than by eye. First fetch the lib — `.review-tools/` is created by the gate's checkout step and does **not** exist in a
clone, so pin-matched fetch is the only way to run it locally:

```bash
gh api "repos/generic-automation-and-it/smooth-ai-report-review/contents/\
.agents/skills/ai-review-report/scripts/lib/extract-review-notes.sh?ref=4bdfea4f361218d88745dfcbad0b00a108a129f2" \
  --jq .content | base64 -d > /tmp/extract-review-notes.sh
```

Then round-trip the body through it — both sections must appear in the output:

```bash
gh pr view <n> --json body --jq .body | bash /tmp/extract-review-notes.sh
```

### Security properties

The gateway's client certificate, private key, private-CA PEM and API key all live inside the
`OPENCODE_VLLM_PROFILE_B64` secret. The bootstrap step base64-decodes it, masks the inner API key with
`::add-mask::`, starts the terminator with `env -u` so the secret is not readable through `/proc/<pid>/environ`, and
deletes the profile and PEM cache as soon as the listener binds — the terminator holds its `SSLContext` in memory by
then. What remains for the life of the job is an unauthenticated loopback listener: anything executing in that job,
including code the review agent runs from the PR under review, can spend gateway quota. That is inherent to running
the gate on a GitHub-hosted runner, not a defect in the step.

Fork pull requests receive no secrets. Repository Actions settings are what keep fork PRs from reaching this job;
the bootstrap step's empty-secret check fails loudly rather than letting the gate proceed toward a socket that will
never answer.

## Publish image

- **Workflow:** `.github/workflows/publish-image.yml`
- **Triggers:** only `push` to `main`, normally produced by merging a PR. Tag pushes and manual dispatch cannot publish. Enforce PR-only changes to main with branch protection; the trigger also covers an allowed direct main push.
- **Permissions:** default `contents: read`; package writes only in image build/promotion jobs. No Git tag or GitHub Release creation jobs or contents-write permission.
- **Platforms:** `linux/amd64,linux/arm64`.
- **Registries:** `ghcr.io/${{ github.repository }}` (API) and the same name suffixed `-apphost` (controller).
- **Gates:** reuse PR build/tests for the same commit, publish immutable candidates, then smoke their exact digests on native amd64/arm64 runners. Controller embeds the API multi-platform digest. Native runner availability must be confirmed for this repository.
- **Identity:** candidates include full SHA, run ID, and attempt. Controller version is `main-<short-sha>`; successful promotion adds `latest` and `sha-<short-sha>` to both images. No SemVer release/tag mechanism is configured.
- **Promotion:** one repository-wide concurrency group with `queue: max` (up to 100 pending runs). Fresh main ref prevents stale `latest` promotion. API and controller alias writes are sequential, not transactional; retain candidate digests to diagnose partial promotion. Use digests when immutable deployment identity is required.
- **Credentials:** smoke uses a temporary dedicated Docker config usable inside Linux controller, not runner/platform credential helpers. Config is deleted after smoke; evidence excludes raw controller logs and login tokens.
- **Alias validation:** promotion requires a nonempty JSON array of valid image tags before either registry is mutated; malformed policy output fails the job rather than producing a successful no-op.
- **Timeouts/cache:** build jobs 45 minutes; smoke 20 minutes per native architecture. Caches are separated by image/ref and by architecture for PR builds.
- **Validation caveat:** older actionlint builds reject `concurrency.queue`; current GitHub Actions documentation supports it. Validate other workflow diagnostics normally, not by deleting the queue policy.
- **Local run / configuration contract:** [docker.md](./docker.md).
