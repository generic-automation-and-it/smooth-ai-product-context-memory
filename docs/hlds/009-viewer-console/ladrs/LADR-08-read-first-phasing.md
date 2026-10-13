# LADR-08 — Read-first phasing, value measured before the write half

## Context

The store is agent-first; whether a human browse surface is genuinely useful for a single-user local
store is not yet evidenced. The prior braindump deferred a memory viewer as "nice to have, not
recommended"; the deferral is lifted post-MVP, but the value claim still needs to be observed.

## Decision

Phase: P1 browse + filters → P2 detail + provenance → P3 graph → P4 management. Each phase is a working,
usable surface. The write half (P4) is built only after P1–P3 show real use, because the write half is
the costly part (bridge + gates).

## Consequences

- Cheap adoption first; value observable with the read half alone.
- The write half (bridge + gate integration) is deferred until it is evidenced.
- Phasing is a decision, not a launch plan: each phase is independently usable.

## Status

Draft
