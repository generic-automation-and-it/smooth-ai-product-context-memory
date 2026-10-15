# AGENTS.md - Shared Skill Code Distribution

AI Context: HLD for Shared Skill Code Distribution. Updated: 2026-10-14

> AI-coder context for this HLD. Architecture diagrams live in [`./diagrams/`](./diagrams/),
> decisions in [`./ladrs/`](./ladrs/), quality spec in [`./nfrs/`](./nfrs/). This file is
> guardrails, not narrative — the narrative is in [`./README.md`](./README.md).

## TL;DR

One model for shared skill code (shared folder inside the plugin, WET copies across it); intent in README, decisions in ladrs/, quality bar in nfrs/.

## Non-Negotiables

- Do not collapse the inside/across split — a shared-folder reference inside the plugin and a WET copy across plugins solve different install realities (LADR-01 vs LADR-02).
- Do not duplicate the read client, the write client, or the refusal boundary — one of each, always (LADR-03).
- Do not leave shims at old peer paths — a stale reference must fail loudly, never run a second copy (LADR-01).
- LADRs are Draft status — flag deviations rather than silently overriding.

## Architecture Decisions

Only decisions whose violation produces wrong code. Full records in [`./ladrs/`](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| LADR-01 | Shared scripts live in `_mimisbrunnr-shared/scripts/`; no peer reaches | A peer path couples to another skill's layout and breaks at runtime |
| LADR-02 | Across plugins, WET copies with parity; `ai-understanding` standalone | A shared-folder reference outside its plugin is an install-time absence |
| LADR-03 | Odin owns the contract; one read + one write client, refusal intact | A second client or a parity-held refusal boundary reintroduces the hazards single ownership prevents |

## Key Behaviors

- The shared folder is not a skill: no `SKILL.md`, never loaded as one, never listed in the marketplace manifest.
- Copy direction is always shared folder → plugin; a fix is never made in a WET copy first.
- Saga imports the store client (not just a subprocess call) — the strongest peer coupling in the inventory, and the first reference to rewire.

## Quality Constraints

Measurable NFRs live in [`./nfrs/`](./nfrs/). Constraints that change how code is written:

- Every packaging manifest names the shared folder; a route that drops it breaks all consumers at once.
- Ledger keys stay digests and subjects stay out of files through the move — the privacy rule moves with the code, not just the code.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-14 | HLD authored (B1 decision record: rule, B0 evidence, inventory, invariants) | issue 222 |
