# AGENTS.md - ai-brain-dump

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — switches, phases and guardrails live there; the cost rationale is in [`README.md`](./README.md).

## Non-Negotiables

- **Don't deduplicate the switch rules in SKILL.md.** They are restated on purpose in Modes & Switches,
  Listen and Guardrails — the repetition counters the model's drift toward premature questions and action.
- **A switch change edits three tables.** The switch table lives in SKILL.md, this skill's `README.md`
  (with cost) and `.agents/skills/README.md` → ai-brain-dump switches; updating one leaves the others stale.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-12 | Initial | #40 |
| 2026-09-13 | `--all` switch | #61 |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
| 2026-10-15 | **Vendored into `smooth-ai-product-context-memory`; ownership taken over here.** Fixes land in this repo, never upstream. Rows above predate the move and reference `smooth-devex-template` issues. | this repo |
