# LADR-08: Load breadth is a filter, not two products

**Status:** Accepted

## Context

BRD-003 BR-42 wants an agent to inherit with controlled breadth: everything (memory + understanding), or
only the distilled Understandings. The temptation is to make these two separate operations, or two separate
load paths.

But both are the same read over the same store, differing only in whether `kind = understanding` is
included. Splitting them into two products would duplicate the read path, and would let them drift — a fix
to one load path not reaching the other.

## Decision

Breadth is a **single load load operation with a filter**:

- `--all` returns memory **and** understanding-kind records (the union).
- A load **without** `--all` (or a targeted flag) returns **only** the understanding-kind.

Both are non-destructive (NFR-01): they read the store and write nothing.

## Alternatives Considered

- **Two separate load commands** — rejected: duplicate the read path and let them drift.
- **`--all` as the only mode** — rejected: an agent inheriting everything cannot be given just the
  distilled skill.
- **Breadth as a selectors-side concern** — rejected: `--tickets`/`--tags` bind imported material to work;
  breadth controls how much is *returned*. The two are orthogonal.

## Consequences

- One load path, one filter; `--all` is the union, the default is understanding-only.
- Both modes write nothing (NFR-01).
- Breadth and selectors stay orthogonal: selectors bind, breadth filters.

## Related

- **NFR-01** — both breadth modes are non-destructive.
- **BRD-003 BR-42**.

## Evidence (2026-09-26)

L1 `tests/SmoothAiProductContextMemory.Application.ComponentTest/Features/UnderstandingTransferStoreTests.cs`: `Kind = understanding` returns only the understanding and the un-filtered query returns both kinds, with zero writes on either; skill L0 `.agents/skills/mimisbrunnr-understanding/tests/run_tests.py` pins `--all` vs understanding-only rendering.
