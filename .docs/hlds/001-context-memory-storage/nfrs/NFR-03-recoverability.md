# NFR-03: Recoverability

**Status:** Draft

## Requirement

The store spans **two systems** — a database and an object store — and a restore must produce a
mutually consistent state.

- A restore yields a database in which **every content reference resolves** to an object present in the restored object store.
- A restore into an empty environment reproduces the source's record and object counts.
- The procedure is **written down and executed at least once**, not assumed to work.

## Verification

- Populate both stores, take a backup of each, restore into an empty environment, then assert: record counts match, object count matches, and **every content reference resolves**.
- Record the procedure and the ordering it depends on.

The dangling-reference check is the one that matters. A database restored from a point after its
matching object-store snapshot yields rows whose bodies do not exist — and nothing surfaces that until
the first drill-down, which is the worst moment to discover it.

## Acceptance Criteria

- Restore verification passes with zero unresolvable references.
- The procedure exists as a document, and has been run end-to-end at least once.
- Ordering and any acceptable skew between the two snapshots is stated explicitly.

## Applies To

Goal 4; LADR-06. Consequence of choosing an external object store over a single-file engine (LADR-01).
