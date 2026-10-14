# NFR-04: Shared folder on every route

**Status:** Draft

<!-- One file per quality attribute. This NFR is a horizontal concern spanning the whole HLD. Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

All three distribution routes resolve the shared folder: a marketplace install contains
`_mimisbrunnr-shared/scripts/` beside the skills; a packed npm tarball lists the shared
folder and the launchers import through it; the `.claude/skills` and `.codex/skills`
mirrors resolve the shared path with no copy step.

## Verification

Per route, with evidence: installed plugin tree inspected for the shared scripts (as in
B0); `npm pack --dry-run` output lists the folder and a launcher import runs; mirror
resolution asserted by same-inode check. Any route dropping the folder fails its check.

## Acceptance Criteria

- Marketplace cache holds the shared scripts; npm tarball lists them; both mirrors resolve them.
- Removing the folder from any route manifest fails that route's check before merge.

## Applies To

Goal 4; LADR-01 and the B0 install evidence.
