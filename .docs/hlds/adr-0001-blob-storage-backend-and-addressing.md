# ADR-0001: Blob storage backend and content addressing

**Status:** Accepted
**Date:** 2026-08-30

## Context

Context-memory documents and attachments must be stored outside PostgreSQL so the database stays an
index (references) rather than a content store. The storage backend must be a containerised
S3-compatible object store so moving to real cloud object storage later is a configuration change, not
a rewrite. The abstraction must not leak engine concepts to callers.

## Decision

- **Backend: MinIO** (S3-compatible, registered via Aspire `AddContainer`) in both the dev AppHost and
  the test framework.
- **Content addressing:** the object key is the SHA-256 of the raw content, sharded as `ab/cd/abcd…`.
  Identical content written twice produces one object and one address; writes are idempotent and safe
  to retry. Orphan objects (unreferenced after the last DB row disappears) are tolerated for now; a
  future sweep is the eventual remedy.
- **Client-side compression:** gzip, applied before upload and reversed on read, recorded in object
  metadata, so behaviour is identical across backends.
- **Bucket topology:** a single bucket per environment; test classes isolate via unique per-test bucket
  names created lazily by the adapter.

## Alternatives considered

- **SeaweedFS (initial selection, rejected).** Its S3 gateway requires a JSON credentials file
  (`-s3.config`) and an admin JWT to create buckets — neither is expressible as a simple environment
  variable, so automating it inside an Aspire dev/test container is impractical. MinIO takes fixed
  `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD` credentials deterministically. SeaweedFS offers Apache-2.0
  against MinIO's AGPLv3, but that licence advantage does not justify the automation cost for a
  local-first single-user service.
- **Mutable human-readable paths.** Rejected — a directory of readable names invites collisions and
  breaks idempotency; content addressing makes writes naturally idempotent and retriable.
- **gzip vs zstd.** Chosen gzip: built-in, zero extra dependency, universally decodable, adequate for
  small text documents. zstd would improve ratio/speed at the cost of a dependency; the abstraction
  keeps the algorithm swappable.

## Consequences

- The Host and tests consume blob storage only through `IBlobStorage`; swapping MinIO for another
  S3-compatible store (or cloud S3) is a configuration and registration change.
- A separate `mc`/init container is not required — the adapter creates its bucket lazily on first write.
- Content-addressed blobs become unreferenced after the last row pointing at them disappears; orphans
  are tolerated and a GC sweep is deferred.
