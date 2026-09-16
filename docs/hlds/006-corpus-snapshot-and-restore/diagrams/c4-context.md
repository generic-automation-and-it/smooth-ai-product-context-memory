# Diagrams — Corpus snapshot and restore

## System Context (C1)

The snapshot tooling is an operator-facing surface delivered through the one published Host
image: preflight and snapshot are HTTP endpoints on the running container; verify and restore
run as one-shot containers from the same image, needing no live service (LADR-07). It reads
both stores, writes only the artefact, and restores only into an explicitly empty target.

```mermaid
C4Context
    title Corpus snapshot and restore — System Context

    Person(practitioner, "Practitioner", "Sole user. Takes, verifies and restores snapshots; reads the preflight report. The skill may invoke preflight/snapshot as a thin caller.")

    System(snapshot, "Corpus snapshot & restore", "Preflight + snapshot: HTTP on the running Host. Verify + restore: one-shot containers, same image. Read-only against live stores; writes only the artefact or an empty restore target.")

    System_Ext(pg, "PostgreSQL + AGE", "Relational rows, graph vertices and edges. Defines corpus membership.")
    System_Ext(blob, "Object storage", "Content-addressed bodies, referenced by hash.")
    SystemDb_Ext(artefact, "Snapshot artefact", "Single archive: database capture + referenced bodies + self-verifying manifest. Sensitive by default; local mounted volume, never synchronised.")

    Rel(practitioner, snapshot, "preflight / snapshot", "HTTP, running Host")
    Rel(practitioner, snapshot, "verify / restore", "docker run --rm, one-shot")
    Rel(snapshot, pg, "Captures state; walks blob references", "read-only")
    Rel(snapshot, blob, "Fetches referenced objects", "read-only")
    Rel(snapshot, artefact, "Writes and verifies", "mounted volume")
    Rel(artefact, snapshot, "Restores into empty target", "explicit action, no API serving")
```

## Flow — snapshot, verify, restore

One diagram earns its place beyond C1: the three claims (membership, integrity, sufficiency)
have three different owners, and the flow shows where each is established — which is the
design's thesis.

```mermaid
flowchart TD
    A[Snapshot requested] --> B[Capture consistent database state<br/>rows + vertices + edges]
    B --> C[Walk blob references from captured state]
    C --> D{Reference resolves?}
    D -->|no| E[Record dangling reference<br/>report, never repair]
    D -->|yes| F[Add object to archive<br/>hash must equal address]
    C --> G[Count unreferenced objects<br/>report, never delete]
    E --> H[Write manifest: entries + hashes + counts + exclusions]
    F --> H
    G --> H
    H --> I[(Archive)]

    I --> J[Verify: recompute hashes,<br/>reconcile counts — offline]
    J -->|mismatch| K[Fail loudly, name entry,<br/>classify corruption vs capture-time]
    J -->|clean| L[Archive trusted]

    I --> M{Restore target empty?}
    M -->|no| N[Refuse without explicit override]
    M -->|yes| O[Restore both stores]
    O --> P[Printed reconciliation:<br/>counts, blob resolution, bounded traversal]
    P -->|closes| Q[Restore certified]
    P -->|does not close| R[Restore failed — no silent partial]
```
