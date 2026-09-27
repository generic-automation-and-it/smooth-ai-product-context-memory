# AI Skills

Self-contained skills for Claude Code, GitHub Copilot, and OpenAI Codex providing specialized workflows and tools.

Skills live **flat**, one directory per skill directly under `.agents/skills/`. Each folder name is **category-prefixed** (`agile-`, `ai-`, `context-`, `git-`) so the listing groups by category when sorted. The prefix is the only grouping mechanism — there are no category subfolders (Claude Code discovers skills exactly one level under `.claude/skills/`).

## Quick Reference

| Skill | Purpose | Usage |
|-------|---------|-------|
| **agile-github-breakdown** | Turn a braindump or existing Feature into GitHub Feature + Task issues | `/agile-github-breakdown` |
| **agile-github-task-from-diff** | Create a GitHub Task (sub-issue) from the current git diff vs main | `/agile-github-task-from-diff [--feature-issue <n> \| --noparentid]` |
| **mimisbrunnr-vitsmunir-dump** | AI Brain / Intelligence Dump — listen-first capture; synthesize on request. Vitsmunir is Old Norse for intelligence, wits, and the power of comprehension. | `/mimisbrunnr-vitsmunir-dump [--oktoask] [--thinking] [--oktoreaddocs] [--oktowebsearch] [--all]` |
| **ai-review** | Analyze and execute AI PR review feedback (fix/skip) | `/ai-review <pr> [1=fix 2=skip …]` |
| **ai-terse** | Reformat this turn's reply into terse, high-density output with a TL;DR | `/ai-terse` |
| **ai-template-sync** | UPSERT smooth-devex-template scaffold into an existing repo | `/ai-template-sync` |
| **context-load-agents-context** | Load ancestor AGENTS.md context for a file | `/context-load-agents-context` |
| **context-load-context** | Load domain context before implementation | `/context-load-context auth` |
| **mimisbrunnr-context-memory** | Get/set persistent context-memory records; sole authority on the write path to the store | `/mimisbrunnr-context-memory [--dryrun] [--approve]` |
| **mimisbrunnr-ymir-bootstrap** | Build a cited, reviewable durable-context baseline for one existing project, optionally focused on its next feature. Ymir is the first being of Norse myth, from whose body the gods shaped the world — as this baseline is shaped from the repository that already exists. | `/mimisbrunnr-ymir-bootstrap <repository> [next-feature focus]` |
| **create-hld** | Author a design-only High-Level Design under `docs/hlds/NNN-<slug>/` | `/create-hld <kebab-slug>` |
| **git-commit** | Commit with conventional format | `/git-commit [--autonomous]` |
| **git-commit-push** | Commit and push to remote | `/git-commit-push [--autonomous]` |
| **git-commit-push-pr** | Commit, push, and create/update PR | `/git-commit-push-pr [--autonomous]` |
| **git-commit-review-push** | Commit, push, and embed `/ai-review` trigger for a full AI review | `/git-commit-review-push [--issue <n>]` |
| **git-sync** | Sync with main (optionally auto-resolve conflicts) | `/git-sync` |
| **manage-rule-system** | Create/update rule files in `.agents/rules/` | `/manage-rule-system` |

### mimisbrunnr-vitsmunir-dump switches

Default (no switch) is pure silent listen-first — no questions, no tools — until you ask it to synthesize.
Opt-in switches relax that, at different token costs (see `mimisbrunnr-vitsmunir-dump/README.md` for the full breakdown):

| Switch | Effect | Cost |
|--------|--------|------|
| _(none)_ | Capture silently; never ask, never browse | baseline |
| `--oktoask` | Ask sparse, non-blocking, tool-free clarifying questions on genuine blockers | small |
| `--thinking` | Make questioning liberal (ask on any unclear/detail gap); implies `--oktoask` | moderate |
| `--oktoreaddocs` | May read local code/docs to ground a question; implies `--oktoask` | large |
| `--oktowebsearch` | May web-search to ground a question; implies `--oktoask` | large |
| `--all` | Enable every other switch (`--oktoask` `--thinking` `--oktoreaddocs` `--oktowebsearch`) | large |

The tool switches (`--oktoreaddocs`, `--oktowebsearch`) re-enable the file/web payload bloat the
listen-first default avoids — use deliberately.

### mimisbrunnr-context-memory switches

Default (no switch) accumulates candidate facts silently during work and writes them in one transaction at
an explicit end-of-task `set`. See `mimisbrunnr-context-memory/README.md` for the cost model — **every write is an LLM
call** (R13), so the switches trade inspection against irreversibility, not against speed:

| Switch | Effect | Cost |
|--------|--------|------|
| _(none)_ | Silent capture; one transactional write at the `set` checkpoint | baseline (1 LLM call per fact) |
| `--dryrun` | Full pipeline, digest rendered, **nothing persisted** — the only pre-write veto point | same as a real write |
| `--approve` | Write `rule`/`nfr`/`decision` as `approved` instead of `proposed` | no extra tokens; widens what becomes citable canon |

`--dryrun` and `--approve` are **mutually exclusive** — one writes nothing, the other is the permission to
write canon. A plain `set`'s digest is a receipt, not a gate: it is rendered after the transaction commits.

### mimisbrunnr-ymir-bootstrap capability gate

See the [practical guide](mimisbrunnr-ymir-bootstrap/README.md) for prompts, preview review, reruns, and
troubleshooting.

Bootstrap always produces an offline candidate preview before storage. Comparison with existing memory,
capture, and recall verification require the registered capability-limited context-memory workers. A new
project starts storage only after explicit approval, which may include creating its durable group; that
group can remain if later capture pauses or fails. The full memory-set `--dryrun` is available only for an
existing group, or after separately authorized group creation, so it must not be presented as guaranteeing
zero mutations overall. When the workers are unavailable, the skill reports those stages as unavailable
and stops with the preview; it never falls back to a direct client or claims that the baseline was stored
or verified.

### git-commit / git-commit-push / git-commit-push-pr switches

The `--autonomous` switch suppresses all interactive questions across the entire commit chain. When passed, the agent uses its best judgment on commit grouping, message selection, and PR title — it never stops to ask.

| Switch | Effect |
|--------|--------|
| _(none)_ | Default — ask for clarification when grouping is unclear or a conforming message cannot be determined |
| `--autonomous` | **No questions asked.** Agent decides everything autonomously and proceeds without confirmation |

`--autonomous` is forwarded automatically through the skill chain: `git-commit-push-pr` → `git-commit-push` → `git-commit`.

## Effort

Skills never pick a model, and never differ per provider — every skill runs on whatever model the session already uses. Each SKILL.md instead carries a single `effort` frontmatter field on the standard AI-harness reasoning-effort scale:

| Effort | Use for |
|--------|---------|
| `low` | Script-driven or single-turn work; no deep reasoning |
| `medium` | Structured authoring across a few files or steps |
| `high` | Multi-turn synthesis or judgment calls |
| `xhigh` | Design and planning artefacts — or stored knowledge — that downstream work is built on |
| `max` | Reserved — not set by any skill; the user raises a run to it explicitly |

A harness that honours skill `effort` applies it on invocation; a harness with a coarser scale uses its nearest supported level. When a skill invokes another skill as a sub-agent, run it at the **callee's** `effort` on the session's model. `effort` governs the agent that *drives* a skill — the models the review gate itself calls are chosen separately by its GitHub Variables (`OPENCODE_REVIEW_REPORT_MODEL_*`, see `docs/wiki/ci.md`).

Two parents own most skills here, and their SKILL.md files are kept byte-identical to them: `smooth-devex-template` (the `agile-`, `context-`, `git-commit`/`git-commit-push`/`git-commit-push-pr`/`git-sync`, `ai-terse`/`ai-template-sync`/`ai-understanding`, `create-hld`, `manage-rule-system` and `mimisbrunnr-vitsmunir-dump` ← `ai-brain-dump` skills) and `smooth-ai-report-review` (`ai-review`, `git-commit-review-push`). The `mimisbrunnr-` context-memory skills are owned by this repo.

### Skill effort levels

| Skill | Effort | Rationale |
|-------|--------|-----------|
| **context-load-context** | low | File discovery and loading; no deep reasoning |
| **context-load-agents-context** | low | Script-driven file traversal; no deep reasoning |
| **git-commit** | low | Diff review + conventional commit; straightforward |
| **git-commit-review-push** | low | Mechanical git plumbing — but it still asks when unclear and double-checks before pushing (`smooth-ai-report-review`) |
| **git-sync** | medium | Default path is script-only, but `--fix` resolves merge conflicts by merging the intent of both sides — the effort must cover the heaviest mode |
| **ai-terse** | medium | Not mechanical reformatting: decides what is signal, compresses without changing meaning, and judges Holes/Ignored for the TL;DR |
| **git-commit-push** | medium | Branch rename logic + upstream tracking |
| **git-commit-push-pr** | medium | PR template authoring + state management |
| **agile-github-task-from-diff** | medium | Diff classification + issue authoring |
| **manage-rule-system** | medium | Cross-tool frontmatter authoring |
| **ai-template-sync** | medium | `sync.sh` does the copy/compare; the agent only picks flags, runs the rules-layout pre-flight and builds the conflict table |
| **mimisbrunnr-vitsmunir-dump** | medium | Listen-first capture — `high` would be re-paid on every turn of a long session. Deep reasoning happens downstream, in the skills its output feeds (`ai-understanding`, `agile-github-breakdown`, `create-hld`) |
| **mimisbrunnr-recall-feedback** | medium | Three fixed recall-feedback API queries plus interpretation |
| **ai-review** | high | Review analysis, fix/skip judgment + multi-file code fixes (`smooth-ai-report-review`) |
| **ai-understanding** | high | Judging what qualifies as transferable knowledge + merge/promotion decisions |
| **mimisbrunnr-dossier** | high | Equivalence, contradiction and gap judgement across a whole store slice |
| **mimisbrunnr-understanding** | high | Judgement on understanding vs scoped fact, and capture-path funneling |
| **agile-github-breakdown** | xhigh | Multi-turn FR/NFR → Task graph + GitHub writes |
| **create-hld** | xhigh | Multi-turn clarification gates + architectural judgment (LADRs, NFRs, diagrams) |
| **mimisbrunnr-ymir-bootstrap** | xhigh | Source-backed baseline the store's later sessions build on: consequential questioning, conflict preservation and reviewed candidate selection require product judgement |
| **mimisbrunnr-context-memory** | xhigh | Sole write path to the store every later session builds on: semantic cross-group dedup, link derivation, atomicity splitting and summary/keyword generation |

### Sub-skill invocation

- **git-commit-push** → invokes **git-commit** at `effort: low`
- **git-commit-push-pr** → invokes **git-commit-push** at `effort: medium`

### Frontmatter shape

Every SKILL.md frontmatter uses the same fields, in this order:

```yaml
---
name: <folder-name>
description: <one line; single-quoted only when YAML requires it>
allowed-tools:        # optional; always a block list
  - Bash(<command>:*)
  - Read
effort: <low|medium|high|xhigh>  # one-line rationale
---
```

The two `smooth-ai-report-review` skills keep that parent's shape verbatim — `ai-review` also carries a `switches:` list, and both align the rationale comment to a column.

## Naming & Ordering

Skills are flat under `.agents/skills/`; the category lives in the folder-name prefix so a sorted listing groups by category:

| Prefix | Skills |
|--------|--------|
| `agile-` | `agile-github-breakdown`, `agile-github-task-from-diff` |
| `ai-` | `ai-review`, `ai-terse`, `ai-template-sync`, `ai-understanding` |
| `context-` | `context-load-agents-context`, `context-load-context` |
| `mimisbrunnr-` | `mimisbrunnr-vitsmunir-dump`, `mimisbrunnr-ymir-bootstrap`, `mimisbrunnr-context-memory`, `mimisbrunnr-dossier`, `mimisbrunnr-recall-feedback`, `mimisbrunnr-understanding` |
| `git-` | `git-commit`, `git-commit-push`, `git-commit-push-pr`, `git-commit-review-push`, `git-sync` |
| _(none)_ | `create-hld`, `manage-rule-system` |

A skill's folder name MUST equal its `name:` frontmatter (this is the slash-command name). When adding a skill, pick the prefix of its category and keep the folder one level under `.agents/skills/`.

## About Skills

Each skill is a directory containing:
- **SKILL.md** — The skill definition with workflow steps and `effort` frontmatter
- **AGENTS.md** — Maintenance context for agents *modifying* the skill (coupling, rationale, drift hazards) per `.agents/rules/meta/knowledge-conventional-contexts-quality.instructions.md`
- **agents/openai.yaml** — OpenAI Codex agent registration (display name, description, default prompt; no model)
- **scripts/** — Helper scripts (if applicable)
- **references/** — Reference documentation (if applicable)

Skills are tool-agnostic and work across Claude Code, GitHub Copilot, and OpenAI Codex.
