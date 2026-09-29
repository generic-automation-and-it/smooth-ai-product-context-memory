# NFR-03: Recoverability

**Status:** Closed — HLD-006 accepted 2026-09-29 and its NFR-02 is the governing recoverability
evidence (supersedes [HLD-006 NFR-02](../../../hlds/006-corpus-snapshot-and-restore/nfrs/NFR-02-consistency.md))

> This Draft claim is closed by HLD-006's corpus snapshot and restore. Its "two stores restore to a
> mutually consistent state" requirement is now HLD-006 NFR-02, whose snapshot artefact carries a
> self-verifying manifest and whose restore ends in a printed reconciliation (rows, versions,
> vertices, edges, objects, zero dangling references, bounded traversal). The prior operational
> round-trip script (`scripts/verify-graph-restore.sh`) is superseded by the restore command's
> built-in reconciliation.
>
> **Both claims in that paragraph are true now; neither was when it was written.** It said "closed on
> HLD-006 *acceptance*" while HLD-006 still read `In Discovery` — a requirement closed on a design
> that had not been accepted. It also called the script superseded while the root `AGENTS.md` and
> HLD-003 NFR-03 still presented `verify-graph-restore.sh` as *the* NFR-03 tool. `scripts/AGENTS.md`
> now states the split explicitly: the restore verb is the NFR-03 round-trip, and the script remains
> for a lighter graph-only check.

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
