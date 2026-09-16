# LADR-02: The manifest is content-addressed and self-verifying

**Status:** Draft

## Context

A backup whose integrity can only be established by restoring it cannot be checked cheaply, so
it is checked rarely, so it is discovered corrupt when it is needed. The store's bodies are
already content-addressed (HLD 001 LADR-06), which means half of the verification machinery
already exists as data: every body's correct hash is its database-cited address.

## Decision

**Carry** a manifest inside the archive: one entry per archive member with its content hash, plus
corpus-level counts — memories, versions, vertices, edges, objects — and a statement of what is
deliberately excluded (recall feedback, per HLD 004). Verification recomputes hashes and
reconciles counts using only the archive.

For bodies, the manifest hash must equal the database-cited address captured in the same
archive. Agreement proves cross-store consistency at capture; disagreement is classified as a
capture-time inconsistency, reported distinctly from transit corruption, because the two demand
different responses (investigate the store vs re-take the snapshot).

The manifest carries **no generation timestamp inside hashed content** — stored times are data,
generation time is variance (the same reasoning as HLD 005 NFR-02). The snapshot's date lives in
archive metadata, outside the hashed entries.

## Alternatives Considered

- **Whole-archive checksum only** — detects corruption but cannot name the damaged entry, cannot prove cross-store consistency, and cannot support partial diagnosis.
- **Verification by test restore only** — needs an empty target and the full restore cost; usable, but as the sufficiency check (LADR-05), not the integrity check.

## Consequences

- Integrity is checkable offline, on any machine, with no service running.
- Cross-store consistency stops being a claim in a Draft NFR and becomes an arithmetic identity.
- Manifest production must walk every reference; snapshot cost grows with corpus size — accepted, and the reason incremental snapshots are named as a deliberate non-goal until measured.
