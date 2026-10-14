# NFR-01: No peer reaches

**Status:** Draft

<!-- One file per quality attribute. This NFR is a horizontal concern spanning the whole HLD. Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

Zero occurrences of a peer-skill path reference in shipped skill scripts:
`grep -rn 'parents[2] / "mimisbrunnr-' .agents/skills/*/scripts` returns nothing, and no
`SKILL.md`/`AGENTS.md` procedure cites a `<peer-skill>/scripts/` path except the shared
folder or a WET copy owned under LADR-02.

## Verification

Local `grep` over `.agents/skills/*/scripts` plus a docs grep over skill `SKILL.md` /
`AGENTS.md` procedure blocks; both empty. (A CI grep step makes it permanent.)

## Acceptance Criteria

- Both greps return no lines on the moved tree.
- Reintroducing one peer path fails the check the same day it lands.

## Applies To

Goal 1; every consumer in the LADR-01 inventory table.
