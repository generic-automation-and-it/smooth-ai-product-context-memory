# LADR-06: Bodies in content-addressed object storage; database holds the reference

**Status:** Accepted

## Context

Document bodies and attachments must live outside the database so it stays an index rather than a
content store. The backend must be a containerised S3-compatible object store, so moving to cloud
object storage later is a configuration change rather than a rewrite, and the abstraction must not leak
engine concepts to callers.

## Decision

**Store** bodies in a containerised S3-compatible object store, addressed by the SHA-256 of their raw
content, sharded by hash prefix. The database holds only the address.

Identical content written twice produces one object and one address, so writes are idempotent and safe
to retry. Compression is applied **client-side** before upload and reversed on read, with the encoding
recorded in object metadata, so behaviour is identical across backends rather than depending on the
engine.

The backend in use is MinIO. An Apache-licensed alternative was preferred initially but rejected on a
concrete operational ground: its S3 gateway requires a JSON credentials file and an admin token to
create buckets, neither expressible as a plain environment variable, which makes automated
provisioning inside a development container impractical.

**A consequence must be accepted rather than discovered.** A stored object is immutable and its address
is a function of its content, so content written in error cannot be edited out — only orphaned, leaving
an address that no longer resolves to wanted content. Anything that must never be stored must be
prevented from reaching the write at all.

## Alternatives Considered

- **A filesystem volume** — rejected: no swappable abstraction, and cloud portability would be a rewrite.
- **Mutable human-readable paths** — rejected: invites collisions and breaks idempotency.
- **Server-side compression** — rejected: makes behaviour backend-dependent, defeating the portability the abstraction exists for.
- **Deleting an orphaned object** — rejected: identical bytes share one address, so deleting can destroy content another version still references. Orphaning drops the reference only.

## Consequences

- Writes are idempotent and retry-safe by construction.
- The store is swappable; callers see addresses, never buckets or endpoints.
- **Backup now spans two stores** that must be captured consistently (NFR-03).
- **A hash-addressed object store is not human-navigable**, so inspectability becomes an unpaid debt requiring a generated export (NFR-04).
- Unreferenced objects accumulate; a sweep is deferred rather than pretended.

## Related

- **LADR-02** — the placement rule this is the outermost case of.
- **NFR-05** — the confidentiality implication of immutability.
