# ADR-0001: Blob storage backend and content addressing

**Status:** Superseded — converted to [HLD 001 — Context memory storage](./001-context-memory-storage/)

This ADR and ADR-0002 described a single design in two documents: the database is an index over
content it does not hold, and bodies live in content-addressed object storage. They have been merged
and converted to an HLD.

| This ADR's content | Now in |
|---|---|
| Content addressing, idempotent writes, orphaning | [LADR-06](./001-context-memory-storage/ladrs/LADR-06-content-addressed-blob-storage.md) |
| Backend selection and the rejected alternative | [LADR-06](./001-context-memory-storage/ladrs/LADR-06-content-addressed-blob-storage.md) — Alternatives |
| Client-side compression | [LADR-06](./001-context-memory-storage/ladrs/LADR-06-content-addressed-blob-storage.md) |
| Two-store backup consequence | [NFR-03 Recoverability](./001-context-memory-storage/nfrs/NFR-03-recoverability.md) |
| Opacity of hash-addressed storage | [NFR-04 Inspectability](./001-context-memory-storage/nfrs/NFR-04-inspectability.md) |
| Bucket topology; lazy bucket creation (no init container) | [testing wiki](../wiki/testing.md) — storage isolation |

Retained as a pointer so existing references resolve. Do not extend this file.
