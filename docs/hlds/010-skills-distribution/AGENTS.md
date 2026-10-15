# AGENTS.md - Shared Skill Code Distribution

AI Context: HLD for Shared Skill Code Distribution. Updated: 2026-10-14

> AI-coder context for this HLD. Architecture diagrams live in [`./diagrams/`](./diagrams/),
> decisions in [`./ladrs/`](./ladrs/), quality spec in [`./nfrs/`](./nfrs/). This file is
> guardrails, not narrative — the narrative is in [`./README.md`](./README.md).

## TL;DR

One model for shared skill code (shared folder inside the plugin, WET copies across it); **implemented** — `_mimisbrunnr-shared/scripts/` ships. Intent in README, decisions in ladrs/, quality bar in nfrs/.

## Non-Negotiables

- Do not collapse the inside/across split — a shared-folder reference inside the plugin and a WET copy across plugins solve different install realities (LADR-01 vs LADR-02).
- Do not duplicate the read client, the write client, or the refusal boundary — one of each, always (LADR-03).
- Do not leave shims at old peer paths — a stale reference must fail loudly, never run a second copy (LADR-01).
- LADRs are **Accepted**. Flag deviations rather than silently overriding them, and record the deviation in this file's changelog.
- `_mimisbrunnr-shared/` is not a skill and never becomes one: no `SKILL.md`, no `skills`-array entry, no slash command. Its context lives in `_mimisbrunnr-shared/AGENTS.md`.

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
| 2026-10-14 | **B2 landed; HLD promoted to Accepted — implemented.** `.agents/skills/_mimisbrunnr-shared/scripts/` now holds nine scripts plus `conflict_menus.json` moved out of odin's folder, the Heimdallr reporter moved out of its own skill's, and kvasir's WET `decisions_rubric.json` / `session_marker.py` collapsed into the shared copy. The three peer reaches are gone (heimdallr's redactor, kvasir's client, saga-dossier's composer), `ai-understanding`'s parity harness compares against the shared copy, `package.json#files` carries the folder, and the PR gate gained a peer-reach grep. NFR-04 was re-verified on all three routes with the real move in a clean-profile marketplace install, an `npm pack`/install smoke, and the symlinked mirrors. | issue 222 |
| 2026-10-14 | HLD authored (B1 decision record: rule, B0 evidence, inventory, invariants) | issue 222 |
