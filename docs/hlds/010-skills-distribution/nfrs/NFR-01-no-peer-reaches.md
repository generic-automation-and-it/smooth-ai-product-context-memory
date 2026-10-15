# NFR-01: No peer reaches

**Status:** Accepted

<!-- One file per quality attribute. This NFR is a horizontal concern spanning the whole HLD. Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

Zero occurrences of a peer-skill path reference in shipped skill scripts:
`grep -rn 'parents[2] / "mimisbrunnr-' .agents/skills/*/scripts` returns nothing, and no
`SKILL.md`/`AGENTS.md` procedure cites a `<peer-skill>/scripts/` path except the shared
folder or a WET copy owned under LADR-02.

## Verification

A PR-gate step greps `.agents/skills/*/scripts` for `parents[2] / "mimisbrunnr-…"` and for any
`mimisbrunnr-*/scripts/` peer path and fails the job on a hit — so the property is permanent rather
than a check someone remembers to run. The docs half (no `SKILL.md` / `AGENTS.md` procedure citing a
`<peer-skill>/scripts/` path) is reviewed per change and was swept in the same commit as the move.

## Acceptance Criteria

- Both greps return no lines on the moved tree.
- Reintroducing one peer path fails the check the same day it lands.

## Applies To

Goal 1; every consumer in the LADR-01 inventory table.
