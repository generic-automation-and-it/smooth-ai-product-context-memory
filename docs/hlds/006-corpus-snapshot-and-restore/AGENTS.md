# AGENTS.md - Corpus snapshot and restore

AI Context: HLD for corpus snapshot and restore. Updated: 2026-09-16

> AI-coder context for this HLD. Architecture diagrams live in [`./diagrams/`](./diagrams/),
> decisions in [`./ladrs/`](./ladrs/), quality spec in [`./nfrs/`](./nfrs/). This file is
> guardrails, not narrative — the narrative is in [`./README.md`](./README.md).

## TL;DR

One portable, self-verifying archive backing up both stores — relational + graph in PostgreSQL,
content-addressed bodies in object storage — with verified restore and orphan accounting. Intent
in [README.md](./README.md); decisions in [./ladrs/](./ladrs/); quality bar in [./nfrs/](./nfrs/).
Business authority is [BRD-001](../../brd/001-context-memory/) — principally `BR-37` (the store
survives the loss of its machine), supported by `BR-13` and `BR-16`; it closes
HLD 001 NFR-03's Draft recoverability claim for the snapshot path.

**This HLD is In Discovery.** All LADRs are Draft — flag deviations rather than silently overriding.

## Non-Negotiables

- **Never grow the forensic dump or the contextual export into this.** Three artefacts, three contracts. The dump is a readable projection, the export is scope-filtered; the snapshot is bytes-faithful and complete, including hidden material — which is why it is sensitive by default (LADR-01).
- **Never enumerate the blob set by listing the object store.** Membership comes from the captured database state only; the difference between the two enumerations is the report, not the archive (LADR-03).
- **Never delete an object, on any path, behind any flag.** Orphan accounting reports; the GC sweep is a separate deferred design. A dangling reference is reported, never repaired (LADR-06).
- **Never restore into a non-empty target without an explicit override.** Merge has no defined semantics; silent merge is data loss on the path that exists to prevent it (LADR-05).
- **Never put a generation timestamp inside hashed manifest content.** Stored times are data; generation time is variance. The snapshot date lives in archive metadata outside the hashed entries (LADR-02).
- **Never log or print memory content** — subject, claim, body, edge reason — from any command, including error paths. Identifiers, hashes and counts only (NFR-03; same rule as HLD 001 NFR-05).
- **Never schedule, prompt for, or side-effect a snapshot.** Staleness is reported by the preflight check; recurrence is the practitioner's decision (LADR-04, BR-01).
- **A blob-hash/address mismatch at capture is not corruption.** Classify and report it as capture-time cross-store inconsistency — it demands investigation of the store, not a retaken snapshot (LADR-02).
- **Recall feedback is excluded from capture and its absence is asserted, not reported as loss** (HLD 004: disposable, outside backup).
- **Never expose verify or restore as HTTP endpoints.** Restore under a serving API drops the database beneath an active connection pool; HTTP verify asks the system whose survival is in question to certify its own backup. They run as one-shot containers from the same image (LADR-07).
- **The container entrypoint verb and the dev CLI verb are one code path.** A second implementation of any verb is drift waiting to be discovered during a disaster (LADR-07).

## Architecture Decisions

See [./ladrs/](./ladrs/). All Draft.

| LADR | Decision | Why it matters |
|------|----------|----------------|
| [LADR-01](./ladrs/LADR-01-third-artefact-not-a-projection.md) | Third artefact, backup semantics | A projection path grown into a backup under-captures; a backup path grown into a projection over-discloses |
| [LADR-02](./ladrs/LADR-02-manifest-is-content-addressed-and-self-verifying.md) | Self-verifying content-addressed manifest | Integrity checkable offline; cross-store consistency becomes an arithmetic identity, not a Draft claim |
| [LADR-03](./ladrs/LADR-03-database-state-defines-corpus-membership.md) | Database state defines membership | The archive holds the corpus, not the corpus plus orphan debris; discrepancies are reported, not stored |
| [LADR-04](./ladrs/LADR-04-visible-staleness-not-scheduled-backup.md) | Visible staleness, no schedule | Any recurring task fails BR-01; reported age makes neglect informed instead |
| [LADR-05](./ladrs/LADR-05-restore-reconciles-against-the-manifest.md) | Printed restore reconciliation | A partial restore must be unable to pass silently; the operator sees the arithmetic close |
| [LADR-06](./ladrs/LADR-06-orphan-accounting-is-reporting-only.md) | Accounting reports, never deletes | Producing the GC measurement must not quietly become performing the sweep |
| [LADR-07](./ladrs/LADR-07-surfaces-split-by-liveness.md) | HTTP for preflight/snapshot; one-shot containers for verify/restore | The disaster path must have fewer prerequisites than the thing it recovers; users run the Docker release, not `dotnet` |

## Key Behaviors

- **Bodies verify double-strength.** Content addressing means a body's correct hash already exists as its database-cited address; verification checks recomputed hash against both the manifest and the address, and the two failure classes are reported distinctly.
- **The membership walk produces the orphan measurement for free.** HLD 001's deferred GC sweep was waiting for exactly this data; every snapshot updates it.
- **Verify needs nothing but the archive** — no service, no database, no network. A verify path that connects to anything is a defect.
- **The preflight check is the natural home for operational self-checks** — snapshot age, orphan counts, and potentially the HLD 003 NFR-04 version-pairing posture. Keep it read-only; it must never become a maintenance actor.
- **Snapshot cost grows with corpus size, deliberately.** Incremental/differential snapshots are a named non-goal until a measured full-snapshot time justifies them — do not build layering speculatively.
- **The archive is a single tar with a JSON manifest.** One entry per member (relational capture, graph capture, each referenced blob body) plus `manifest.json`, which lists each entry with its SHA-256 hash and the corpus-level counts. Blob bodies are content-addressed under their cited address. The manifest carries no generation timestamp inside hashed content — the snapshot date lives in archive metadata only.
- **Capture is one consistent database snapshot.** Relational rows, AGE graph and the blob reference walk are read against one `REPEATABLE READ` snapshot on a single Npgsql connection, so the blob set and the state citing it describe the same moment (LADR-03). Blob bodies are then read through `IBlobStorage`.
- **Preflight serves the last snapshot's stored result, not a fresh walk.** A per-request full corpus walk would make a casually-called check expensive; preflight reports the last snapshot's count/orphan numbers plus how old it is, persisted in a gitignored metadata file written by the snapshot (LADR-04).

## Quality Constraints

Targets and verification live in [./nfrs/](./nfrs/). Two shape how code is written:

- **Snapshot and preflight are provably read-only against both stores** — verified by byte-equality of rows and version chains before and after, the same mechanism HLD 005 NFR-06 uses for exports (NFR-04).
- **The default artefact destination is gitignored and unsynchronised**, and the destination is printed at creation. A snapshot landing somewhere committed or synced is a confidentiality failure, not a convenience (NFR-03).

## Migration Plans

- On acceptance, HLD 001 NFR-03 (Recoverability, Draft) is closed by this design's NFR-02 and must be updated in the same change to point here.
- `scripts/verify-graph-restore.sh` and `scripts/seed-graph-sample.sh` remain as operational tooling until the restore command's built-in reconciliation supersedes the former; record the supersession in `scripts/AGENTS.md` when it happens.
- The deferred GC sweep (HLD 001 migration plan) becomes designable once snapshot orphan accounting has produced growth data; it is a separate future HLD, not an extension of this one.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-09-24 | Implementation delivered: one tar + JSON manifest per snapshot (archive format documented in Key Behaviors), capture from a single `REPEATABLE READ` snapshot, offline `verify` with tamper suite, `restore` with printed reconciliation, HTTP preflight/snapshot endpoints, one-shot container/CLI verbs, and a persisted last-snapshot metadata store for preflight. | HLD-006, BR-37 |
| 2026-09-16 | Created — discovery HLD for verified corpus snapshot and restore. Motivated by reuse analysis of forkd's manifest-verified snapshot-pack pattern mapped onto HLD 001 NFR-03 (Draft) and BRD-001's recorded durability gap. | BRD-001; HLD 001 NFR-03 |
| 2026-09-16 | LADR-07 added: surfaces split by liveness — preflight/snapshot as HTTP on the running Host, verify/restore as one-shot containers from the same image. Driven by the Docker-release constraint (no SDK on user machines); C1 and NFR-04 updated to match. | LADR-07 |
