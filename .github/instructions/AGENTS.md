---
description: 'Index of every rule file in .agents/rules/ — table split by folder, frontmatter fields as columns'
globs: "**"
paths:
  - "**"
applyTo: '**'
alwaysApply: true
---
# Rules Index

## TL;DR

Index of every `*.instructions.md` in `.agents/rules/` (→ `.github/instructions/`), split by folder, with each file's frontmatter fields as columns.

## Non-Negotiables

- The tables MUST reflect the actual rule files — regenerate them from frontmatter after adding, removing or renaming a rule file. Never hand-edit rows; a stale index is worse than none.
- Column values are the file's own frontmatter fields. Do not invent or normalise them.

## Key Behaviors

- Scoping is **per-file** frontmatter: `paths` (Claude Code), `globs` + `alwaysApply` (Cursor), `applyTo` (Copilot). Category subfolders are organizational only — they do not change loading.
- `code-review-standards` is hook-deferred for Claude (non-matching `paths` sentinel, re-injected by a `UserPromptSubmit` hook) and always-loaded by Cursor and Copilot.
- Rule-file convention: `meta/rules`.

### _(cross-cutting)_

| Rule | description | globs | paths | applyTo | alwaysApply |
|---|---|---|---|---|---|
| `ai-workflow-rules` | 'AI-assisted coding workflow phases (Odin→Bragi) — mandatory execution order for all coding tasks' | `"**"` | `"**"` | `'**'` | `true` |
| `clean-code` | 'Clean Code baseline (Robert C. Martin) — intent-revealing names, one-thing functions, self-documenting code over comments, tell don''t ask, Boy Scout Rule' | `"**"` | `"**"` | `'**'` | `true` |
| `code-review-standards` | 'Code review standards and intentional design decisions — prevent false positive AI review feedback' | `"**"` | `".review-only--injected-via-hook"` | `'**'` | `true` |
| `project-overview` | 'SmoothAiProductContextMemory backend tech stack, architecture, and commands for AI coding tasks' | `"**"` | `"**"` | `'**'` | `true` |
| `solid-principles` | 'When SOLID is a real diagnosis and when applying it makes the code worse — premature abstraction, ADR precedence, the triggers worth acting on' | `"**"` | `"**"` | `'**'` | `true` |

### meta

| Rule | description | globs | paths | applyTo | alwaysApply |
|---|---|---|---|---|---|
| `knowledge-conventional-contexts-quality` | 'AGENTS.md quality standards, required structure, and anti-patterns' | `"**/*AGENTS.md"` | `"**/*AGENTS.md"` | `'**/*AGENTS.md'` | `true` |
| `rules` | 'Convention for creating new rule files in .agents/rules/' | `".agents/rules/**"` | `".agents/rules/**"` | `'.agents/rules/**'` | `false` |
| `understandings` | 'Rules vs Understandings — discovered knowledge is evidence, not orders; how to inherit it at task start and when to promote it' | `"**/*understanding.md"` | `"**/*understanding.md"` | `'**/*understanding.md'` | `false` |

### backend

| Rule | description | globs | paths | applyTo | alwaysApply |
|---|---|---|---|---|---|
| `api-mediator-validation` | 'Backend API rules: minimal APIs in Host, Mediator request flow, and FluentValidation fail-fast pipeline' | `"**/*.cs"` | `"**/*.cs"` | `'**/*.cs'` | `false` |
| `architecture-slices` | 'Backend architecture rules: clean architecture boundaries and application feature-slice structure' | `"**/*.cs"` | `"**/*.cs"` | `'**/*.cs'` | `false` |
| `backend-logging-conventions` | 'Backend logging level conventions — default to Debug, reserve Information for operation boundaries' | `"**/*.cs"` | `"**/*.cs"` | `'**/*.cs'` | `false` |
| `external-api-clients` | 'Conventions for Refit-based external API clients: file locations, interface shape, caching adapter, sync (direct) registration' | `"**/*.cs"` | `"**/*.cs"` | `'**/*.cs'` | `false` |
| `migrations` | 'Backend migration rules: EF Core migration authoring conventions and code-coverage exclusion requirements' | `"src/*.Infrastructure/Persistence/Migrations/**/*.cs"` | `"src/*.Infrastructure/Persistence/Migrations/**/*.cs"` | `'src/*.Infrastructure/Persistence/Migrations/**/*.cs'` | `false` |
| `readonly-collections` | 'Materialise no-tracking EF reads as arrays; List only when the collection is mutated' | `"**/*.cs"` | `"**/*.cs"` | `'**/*.cs'` | `false` |

### skills

| Rule | description | globs | paths | applyTo | alwaysApply |
|---|---|---|---|---|---|
| `skill-secret-handling` | 'How AI agent skills must handle secrets — read from the runtime environment via a script, never embed secret values in model-visible or committed text.' | `".agents/skills/**"` | `".agents/skills/**"` | `'.agents/skills/**'` | `false` |

### git

| Rule | description | globs | paths | applyTo | alwaysApply |
|---|---|---|---|---|---|
| `git-policy` | 'Git operations policy — never commit/push without explicit user request; branching strategy, Conventional Commits for messages, ticketed PR titles' | `"**"` | `"**"` | `'**'` | `true` |
| `pr-standards` | 'Pull request standards — GitHub PR workflow, Conventional Commits title format, AI Review Notes' | `"**"` | `"**"` | `'**'` | `true` |

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-15 | Created — index of every rule file, split by folder, frontmatter fields as columns. | this PR |

## Installed subset (installer-owned-rules-subset)

| Scope | Rules |
| :---- | :---- |
| `**` | `code-review-standards` · `git/git-policy` |
| `**/*AGENTS.md` | `meta/knowledge-conventional-contexts-quality` |
| `.agents/rules/**` | `meta/rules` |
