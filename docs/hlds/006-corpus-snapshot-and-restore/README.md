# Corpus snapshot and restore — High-Level Design

| | |
|---|---|
| **Status** | In Discovery |
| **Owner** | generik0 |
| **Tracker** | Context-memory operability |
| **Business authority** | [BRD-001 — Cross-product linked context memory](../../brd/001-context-memory/) (`BR-37`, supported by `BR-13`, `BR-16`) |
| **Last updated** | 2026-09-16 |

> Discovery / prototyping HLD. This document delivers **intent + spec** — what we are
> building and why, the decisions behind it, and the quality bar it must meet. It does
> **not** contain an implementation plan; execution (phasing, sub-issues, sequencing) is
> tracked in the issue/work tracker.

## Intent

The store is a personal asset that grows monotonically across two engines — relational rows
and graph edges in PostgreSQL, bodies in content-addressed object storage — and nothing today
protects that asset as a whole. BRD-001's `BR-37` requires an on-demand, verifiable, rebuildable
copy with its recency visible; HLD 001's NFR-03 (two stores restore to a mutually consistent
state) is still Draft, and the only restore evidence is an operational script exercised against
a scratch database, with no artefact format and no way to verify a backup without performing a
restore.

This design introduces the **corpus snapshot**: one portable, self-verifying archive capturing
both stores at one moment, plus a verified restore path. The snapshot carries a manifest with a
content hash per entry, so its integrity — and the cross-store consistency of what it captured —
is checkable structurally, without trusting the process that produced it. Producing the manifest
walks every database-held blob reference against the object store, so dangling references and
unreferenced objects become visible as a byproduct.

This is a **third artefact**, deliberately distinct from the two that exist: the forensic dump
is a readable projection (BR-17), the contextual export is a curated, scope-filtered document
(BRD-002). The snapshot is bytes-faithful backup — it contains everything, including material
hidden from retrieval, which is exactly why it is handled as sensitive by default.

## Key Goals

### 1. One artefact captures both stores at one consistent moment

A backup of the database without the objects it references is an index over nothing; a backup
of the objects without the database is content with no meaning. Today the two stores are backed
up — when they are backed up at all — independently, and nothing asserts they describe the same
moment.

The snapshot captures the relational data, the AGE graph (vertices and edges) and every object
the captured database state references, in a single archive. The blob set is *enumerated from
the captured database state*, not from a directory listing — the database is the index, so the
database decides what belongs to the corpus. Recall feedback is excluded: HLD 004 defines it as
disposable and outside backup by design.

**Acceptance criteria / DoD**

- One command produces one archive containing relational data, graph data and every referenced body.
- The blob set in the archive is derived from the captured database state, never from listing the object store.
- A body referenced by any version — current or superseded — is present; history survives (`BR-13`).
- Recall-feedback records are absent from the archive, and their absence is stated in the manifest.

### 2. Integrity is verifiable without a restore

A backup discovered to be corrupt at restore time is not a backup. The archive carries a
manifest: every entry named with its content hash, plus corpus-level counts (memories, versions,
vertices, edges, objects). Verification recomputes hashes and reconciles counts — no database,
no running service, no restore required.

Bodies are already content-addressed, so for them the check is double-strength: the recomputed
hash must match both the manifest *and* the address the database row cites. A mismatch is not
corruption in transit — it is cross-store inconsistency that existed at capture, surfaced
instead of shipped.

**Acceptance criteria / DoD**

- A verification command validates an archive using only the archive itself.
- Any truncated, altered or missing entry is detected and named; verification of a tampered archive fails loudly.
- A blob whose content hash disagrees with the address the database cites is reported as a capture-time inconsistency, distinct from archive corruption.
- Verification hydrates nothing into a live store and never prints memory content.

### 3. Restore is proven, not assumed

The existing round-trip evidence (HLD 003 NFR-03: rows, vertices, edges, post-restore traversal)
demonstrated restore once, operationally. This design makes that check part of the contract:
restoring an archive ends with a reconciliation against the manifest, and the reconciliation is
printed — counts in the archive, counts in the restored stores, every referenced blob resolving.

**Acceptance criteria / DoD**

- Restore into an empty target reproduces the manifest's counts exactly: rows, versions, vertices, edges, objects.
- Every blob reference in the restored database resolves to an object whose hash matches its address — zero dangling references.
- A bounded traversal succeeds against the restored graph.
- The reconciliation is printed as output, so a human sees the arithmetic close (the same reasoning as HLD 005 NFR-04).
- Restore refuses a target that already holds data unless explicitly told otherwise; it never merges silently.

### 4. Corpus health is visible without scheduled upkeep

`BR-01` forbids designs that require remembered maintenance, and BRD-002 rejects standing
regeneration for the same reason. A backup taken on a schedule the practitioner must maintain
would fail that test. The resolution is visibility, not scheduling: a preflight check reports
the age of the most recent snapshot and the orphan/dangling counts from the last manifest walk,
so staleness is a stated fact the practitioner sees, never a chore they must remember.

Orphan accounting is **reporting only**. Unreferenced objects accumulate by design (orphaning is
the only remedy content addressing allows); this design counts and names them, and deliberately
does not delete them — HLD 001 records why deletion is dangerous (an object may be referenced by
another version) and the garbage-collection sweep remains deferred.

**Acceptance criteria / DoD**

- A check reports: last snapshot age, corpus counts, unreferenced-object count, dangling-reference count.
- The check is read-only against both stores and never prints content.
- No component of this design schedules, prompts for or requires recurring action.
- Nothing in this design deletes an object.

## Core Separation of Concerns

> The database decides what the corpus is; the manifest proves the archive holds it; the restore
> proves the archive is sufficient.

Membership, integrity and sufficiency are three different claims and each has one owner.
Membership is the captured database state — the same "the database indexes content it does not
hold" split HLD 001 rests on, extended to backup. Integrity is the manifest's content hashes,
checkable offline. Sufficiency is the restore reconciliation, checkable only by restoring.
Collapsing any two of these produces a backup that asserts what it cannot prove.

## Guiding Principle — A backup you cannot verify is a hope

> Every claim the snapshot makes is checkable from the artefact or the restore, never from trust
> in the process that produced it.

- The snapshot is bytes-faithful and complete, including hidden and superseded material — it is a backup, not a projection, and is therefore sensitive by default (never committed, never synchronised, destination visible).
- We will deliberately **not** schedule snapshots, prompt for them, or make them a capture side effect. Visibility of staleness replaces recurrence of action.
- We will deliberately **not** delete orphaned objects. Accounting yes, sweeping no — that remains a separate, deferred design.
- We will deliberately **not** build incremental or differential snapshots until corpus size makes full snapshots impractical, measured not assumed.

---

## Diagrams

- [System Context (C1) and the snapshot / verify / restore flow](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADRs 01–04 are strategic (*what* and *why*); 05–07 are tactical (*how*). Each is a single
decision — a horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-third-artefact-not-a-projection.md) | The snapshot is a third artefact — backup semantics, not a projection | Draft |
| [LADR-02](./ladrs/LADR-02-manifest-is-content-addressed-and-self-verifying.md) | The manifest is content-addressed and self-verifying | Draft |
| [LADR-03](./ladrs/LADR-03-database-state-defines-corpus-membership.md) | Captured database state defines corpus membership | Draft |
| [LADR-04](./ladrs/LADR-04-visible-staleness-not-scheduled-backup.md) | Visible staleness, never a scheduled backup | Draft |
| [LADR-05](./ladrs/LADR-05-restore-reconciles-against-the-manifest.md) | Restore ends with a printed reconciliation against the manifest | Draft |
| [LADR-06](./ladrs/LADR-06-orphan-accounting-is-reporting-only.md) | Orphan accounting reports; it never deletes | Draft |
| [LADR-07](./ladrs/LADR-07-surfaces-split-by-liveness.md) | Surfaces split by liveness — HTTP from the running Host; one-shot containers for verify and restore | Draft |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable
target, a verification mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-integrity.md) | Integrity | Any single-byte alteration or missing entry detected offline | Draft |
| [NFR-02](./nfrs/NFR-02-consistency.md) | Consistency | Restored corpus reconciles to the manifest exactly; zero dangling references | Draft |
| [NFR-03](./nfrs/NFR-03-confidentiality.md) | Confidentiality | No content in logs or reports; artefact sensitive by default | Draft |
| [NFR-04](./nfrs/NFR-04-operability.md) | Operability | Snapshot, verify and restore are one command each; no added container; live store untouched by verify | Draft |
