# AGENTS.md - create-hld

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — HLD contract, economy budgets, workflow and quality bar live there.

## Non-Negotiables

- **Upstream `create-hld` diverges; keep the local deltas on any merge back.** Upstream writes singular
  `docs/hld/` and Linear-specific wording; this repo uses `docs/hlds/` (root `AGENTS.md`) and
  tracker-agnostic wording.
- **The budgets and the two tests (specificity, line) stay in `SKILL.md`.** Read every invocation;
  moving them behind a `references/` load makes them skippable, which defeats them.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-16 | Initial; ported from upstream, project-agnostic (`docs/hld`→`docs/hlds`, Linear→tracker) | — |
| 2026-09-13 | Economy bar ported from downstream; `Alternatives Considered` dropped | #64 |
| 2026-10-07 | One AGENTS.md contract: repo rule; script prints it plus HLD delta | #89 |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
