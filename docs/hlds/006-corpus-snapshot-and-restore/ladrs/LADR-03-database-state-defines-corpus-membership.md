# LADR-03: Captured database state defines corpus membership

**Status:** Draft

## Context

The archive must contain the objects the database references — but "the objects" can be
enumerated two ways: walk the database's blob references, or list the object store. The two sets
differ exactly when it matters: unreferenced objects accumulate by design (orphaning is the only
remedy content addressing allows), and a dangling reference is a defect worth surfacing.

## Decision

**Enumerate** the archive's blob set from the captured database state, never from listing the
object store. The database is the index (HLD 001's core thesis); the index decides what belongs
to the corpus. The capture of relational data, graph data and the reference walk happens against
one consistent database snapshot, so the blob set and the state citing it describe the same
moment.

Two discrepancy classes fall out of the walk and are reported, not repaired: a **dangling
reference** (database cites an address the object store cannot resolve — a defect) and an
**unreferenced object** (object exists, nothing cites it — expected debris, counted for the
deferred GC design). Recall feedback is excluded from capture entirely: HLD 004 defines it as
disposable and outside backup, which keeps it out of the cross-store consistency problem.

## Alternatives Considered

- **Archive the whole object store** — captures orphans forever, grows the artefact monotonically with debris, and still proves nothing about references.
- **Union of both enumerations** — inherits the worst of each; discrepancies get stored instead of reported.

## Consequences

- The archive is exactly as large as the live corpus, not the corpus plus its history of mistakes.
- Orphan and dangling accounting comes free with every snapshot — the measurement HLD 001's deferred GC sweep was waiting for.
- A body deleted from the object store but still cited is caught at the next snapshot, not at the next read of that memory.
