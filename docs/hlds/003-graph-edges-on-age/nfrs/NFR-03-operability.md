# NFR-03: Operability

**Status:** Draft

## Requirement

The service remains single-user and local-first. Introducing graph capability must not change its
operational shape:

- **Container count is unchanged** — one API, one database, one object store. Adding a fourth is a failure.
- **One backup covers both models.** A single database backup and restore round-trips relational rows *and* graph edges, with no separate graph export step.
- **Start-up remains one command**, with no manual post-start setup — no attaching to a shell to install an extension or create a graph.
- **First-run time does not regress by more than 30 seconds** against the current baseline, measured from a cold image pull.

## Verification

- Count running containers before and after; any increase fails.
- Back up a populated database, restore into an empty instance, and assert both a relational row count and an edge count match the source. An edge count of zero after restore is the specific failure this catches.
- Start from clean on a machine with no prior image, then immediately exercise one relationship write and one traversal. Any manual step between start and success fails.
- Time the cold-start path and compare against the recorded baseline.

The restore check is the one worth emphasising: a backup that silently omits graph data looks
successful until the first traversal after a restore, which is the worst moment to discover it.

## Acceptance Criteria

- Container count identical to the pre-change topology.
- Restore verification passes with non-zero edge count matching the source.
- A developer with no prior knowledge starts the stack and performs a traversal using only the documented command.
- Cold-start delta recorded and within budget.

## Applies To

Goal 4 (operability does not degrade); LADR-01; LADR-04; the development and test orchestration hosts.
