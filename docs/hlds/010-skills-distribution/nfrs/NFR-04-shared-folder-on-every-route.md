# NFR-04: Shared folder on every route

**Status:** Accepted

<!-- One file per quality attribute. This NFR is a horizontal concern spanning the whole HLD. Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

All three distribution routes resolve the shared folder: a marketplace install contains
`_mimisbrunnr-shared/scripts/` beside the skills; a packed npm tarball lists the shared
folder and the launchers import through it; the `.claude/skills` and `.codex/skills`
mirrors resolve the shared path with no copy step.

## Verification

Per route, with evidence, all three measured 2026-10-14 on the branch carrying the move:

- **Marketplace** — in a clean profile (`HOME` a throwaway directory, Claude Code 2.1.280),
  `claude plugin marketplace add <clone>` plus `claude plugin install mimisbrunnr@mimisbrunnr` and
  `claude plugin install ai-understanding@mimisbrunnr` both succeeded. The plugin cache held the
  **whole repository** — `.agents`, `.claude`, `.codex`, `src`, `tests`, `docs`, `scripts`, `npm` —
  with `_mimisbrunnr-shared/scripts/` present beside the skills holding the real moved files (not a
  dummy probe, as in B0). From the installed tree, the shared reporter resolved and loaded the shared
  `redact.py` through `parents[2] / "_mimisbrunnr-shared"` and reported a real repository with
  `ticketsUnavailable: null`; kvasir's client and saga-dossier's composer imported their shared
  dependencies; the shared read client refused a write token with exit 2.
- **npm** — `npm pack --dry-run` lists the folder; `npm test` packs, installs the tarball in
  isolation and runs the shared reporter through a launcher, resolving the shared redactor.
- **Mirrors** — `.claude/skills` and `.codex/skills` are symlinks to `.agents/skills` (the symlink
  survives a plugin install), so they resolve the shared path by construction with no copy step.

The marketplace `source` is the GitHub URL; the run above pointed `source` at `./` in a throwaway
clone and never in this repository. The real GitHub-URL `source` is still untested and is expected to
clone the whole repository the same way — carried in LADR-01's Open, not claimed.

## Acceptance Criteria

- Marketplace cache holds the shared scripts; npm tarball lists them; both mirrors resolve them.
- Removing the folder from any route manifest fails that route's check before merge.

## Applies To

Goal 4; LADR-01 and the B0 install evidence.
