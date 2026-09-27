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

### git-commit / git-commit-push / git-commit-push-pr switches

The `--autonomous` switch suppresses all interactive questions across the entire commit chain. When passed, the agent uses its best judgment on commit grouping, message selection, and PR title — it never stops to ask.

| Switch | Effect |
|--------|--------|
| _(none)_ | Default — ask for clarification when grouping is unclear or a conforming message cannot be determined |
| `--autonomous` | **No questions asked.** Agent decides everything autonomously and proceeds without confirmation |

`--autonomous` is forwarded automatically through the skill chain: `git-commit-push-pr` → `git-commit-push` → `git-commit`.

## Effort

Skills never choose a model and never switch model per provider — the session's model is the
model. Each `SKILL.md` instead declares one `effort:` level in its frontmatter, on the standard AI
harness effort scale:

| Level | Use for |
|-------|---------|
| `low` | Script-driven or single-turn work with no real judgement |
| `medium` | Structured authoring across a few files or one clear decision |
| `high` | Multi-turn judgement, synthesis, or interactive Q&A |
| `xhigh` | Judgement that downstream work builds on, or an irreversible write path |
| `max` | Never a skill default — the user raises the session to `max` when a task needs it |

The harness applies the level while the skill runs; a harness without a given level uses its nearest
supported one. The user's explicit session effort always wins over a skill's declared level.

### Skill effort classification

| Skill | Effort | Rationale |
|-------|--------|-----------|
| **ai-terse** | low | Single-turn reply reformatting; no tools or deep reasoning |
| **context-load-agents-context** | low | Script-driven file traversal |
| **context-load-context** | low | File discovery and loading |
| **git-commit** | low | Diff review + conventional commit message |
| **git-sync** | low | Fetch + merge; raise the session effort for a hard conflict |
| **agile-github-task-from-diff** | medium | Diff classification + issue authoring |
| **ai-review** | medium | Review analysis + code fixes across multiple files |
| **git-commit-push** | medium | Branch rename logic + upstream tracking |
| **git-commit-push-pr** | medium | PR template authoring + draft/ready state management |
| **git-commit-review-push** | medium | Branch rename + `/ai-review` trigger placement + upstream tracking |
| **manage-rule-system** | medium | Cross-tool frontmatter authoring |
| **mimisbrunnr-recall-feedback** | medium | Three fixed recall-feedback API queries plus interpretation |
| **agile-github-breakdown** | high | Multi-turn FR/NFR → Task graph + GitHub writes |
| **ai-template-sync** | high | Interactive multi-turn Q&A + conditional file sync across tools |
| **ai-understanding** | high | Multi-turn judgement on what qualifies, merge/promotion decisions |
| **mimisbrunnr-dossier** | high | Equivalence, contradiction and gap judgement across a whole store slice |
| **mimisbrunnr-understanding** | high | Judgement on understanding vs scoped fact, and capture-path funneling |
| **mimisbrunnr-vitsmunir-dump** | high | Multi-turn synthesis + deep requirement reasoning |
| **create-hld** | xhigh | Clarification gates + architectural judgement (LADRs, NFRs, diagrams) that downstream work builds on |
| **mimisbrunnr-context-memory** | xhigh | Sole write path: semantic cross-group dedup, link derivation, atomicity splitting and summary/keyword generation — judgement the database cannot express as constraints |

### Sub-skill invocation

A skill that invokes another skill as a sub-agent passes no model. The sub-agent inherits the session
model and runs at the sub-skill's declared effort:

- **git-commit-push** → invokes **git-commit** (`low`)
- **git-commit-push-pr** → invokes **git-commit-push** (`medium`)

## Naming & Ordering

Skills are flat under `.agents/skills/`; the category lives in the folder-name prefix so a sorted listing groups by category:

| Prefix | Skills |
|--------|--------|
| `agile-` | `agile-github-breakdown`, `agile-github-task-from-diff` |
| `ai-` | `ai-review`, `ai-terse`, `ai-template-sync` |
| `context-` | `context-load-agents-context`, `context-load-context` |
| `mimisbrunnr-` | `mimisbrunnr-vitsmunir-dump`, `mimisbrunnr-context-memory` |
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
