# AGENTS.md - ai-ralph-loop

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — switches, the six categories, the evidence bar and the loop live there.

## Non-Negotiables

- **The Evidence bar and the ledger are what make the loop converge.** Loosen the bar, or let reviewers
  skip the ledger, and every pass finds "something" — the run hits the loop cap with churn instead of
  stopping clean.
- **The six categories are the user's spec, not an example list.** Adding or renaming one changes the
  Categories table, the reviewer lens split and the final table's `Category` column together.
- **Reviewers receive `SKILL.md` sections by heading** (Categories, Evidence bar, Reviewer brief).
  Renaming a heading breaks the hand-off silently.
- **New switches narrow or tune; they never gate.** Behaviour the bare run already has stays
  available without any switch.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-16 | Initial: Ralph review→fix→verify loop, `--loops` / `--scope`, fix table | — |
| 2026-10-16 | Shipped by the installer (owned set) | #104 |
| 2026-10-16 | `--spawn` switch: parallel subagent lenses; default stays sequential inline in the current pid | — |
