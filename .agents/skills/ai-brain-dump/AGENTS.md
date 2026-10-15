# AGENTS.md - ai-brain-dump

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — switches, phases and guardrails live there; the cost rationale is in [`README.md`](./README.md).

## Non-Negotiables

- **Don't deduplicate the switch rules in SKILL.md.** They are restated on purpose in Modes & Switches,
  Listen and Guardrails — the repetition counters the model's drift toward premature questions and action.
- **A switch change edits two tables.** The switch table lives in SKILL.md and this skill's `README.md`
  (with cost); updating one leaves the other stale.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-09-13 | `--all` switch | #61 |
| 2026-10-15 | Cut to HLD shape | — |
| 2026-10-15 | Vendored into `generic-automation-and-it/smooth-ai-product-context-memory`; ownership taken over there | — |
| 2026-10-15 | The vendoring row names the owning repository explicitly | — |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
