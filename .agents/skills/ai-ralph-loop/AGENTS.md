# AGENTS.md - ai-ralph-loop

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — switches, the six categories, the evidence bar and the loop live there.

## Non-Negotiables

- **The Evidence bar and the ledger are what make the loop converge.** Loosen the bar, or let reviewers
  skip the ledger, and every pass finds "something" — the run hits the loop cap with churn instead of
  stopping clean.
- **The six categories are the user's spec, not an example list.** Adding or renaming one changes the
  Categories table, the reviewer lens split and the final table's `Category` column together.
- **Reviewers receive `SKILL.md` sections by heading** (Categories, Evidence bar, Test focus, Reviewer
  brief, and active switch sections). Renaming a heading breaks the hand-off silently; omitting an
  active switch section makes spawned and inline reviews follow different policies.
- **Test focus and DR-003 are a coupled contract.** Check
  `.github/instructions/code-review-standards.instructions.md` when changing test policy; a stale
  higher-priority rule can silently suppress the skill's testing findings.
- **New switches narrow or tune; they never gate.** Behaviour the bare run already has stays
  available without any switch.
- **Plan mode's ledger is keyed by section, not by file.** In the `File(s)` column the cell holds the plan
  section heading. An agent matching ledger rows by literal file path finds no duplicate, re-raises a
  settled section, and the run churns to the cap instead of converging.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-16 | Initial: Ralph review→fix→verify loop, `--loops` / `--scope`, fix table | — |
| 2026-10-16 | Shipped by the installer (owned set) | #104 |
| 2026-10-16 | `--spawn` switch: parallel subagent lenses; default stays sequential inline in the current pid | — |
| 2026-10-16 | Test focus hand-off; DR-003 coupling | — |
| 2026-10-16 | `--notests`: active-switch reviewer hand-off | — |
| 2026-10-17 | `--plan [path]` switch: plan/design mode, additive; same loop, evidence bar and convergence | — |
