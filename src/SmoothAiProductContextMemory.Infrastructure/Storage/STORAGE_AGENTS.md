# STORAGE_AGENTS.md

## TL;DR

Blob (object) storage for context-memory **documents and attachments**, keeping document bodies out of
PostgreSQL so the database stays an index rather than a content store. The `IBlobStorage` abstraction
lives in Application; the S3-compatible implementation lives here in Infrastructure.

## Non-Negotiables

- **Content-addressed.** The address is the SHA-256 of the raw (uncompressed) content. Identical content
  written twice yields one object and the same address — writes are idempotent and safe to retry.
- **Client-side compression.** Content is gzip-compressed before upload and transparently reversed on
  read, so behaviour is identical across storage backends regardless of server-side compression.
- **No engine concepts leak.** The `IBlobStorage` interface (`Application/Abstractions/IBlobStorage.cs`)
  never exposes buckets, object keys or ETags — the backing store stays swappable.
- **Bucket created lazily** on first write (`EnsureBucketAsync`); reads treat a missing bucket as a
  missing object. No separate init container required.
- **Options bound from configuration**, populated by Aspire in development (`BlobStorage:Endpoint`,
  `BlobStorage:AccessKey`, `BlobStorage:SecretKey`, `BlobStorage:Bucket`). Validated on start.
- **Never log content or credentials.**

## Contract

- `Store(content, contentType?) -> address` — writes and returns the content address.
- `Get(address) -> BlobContent?` — returns content (decompressed) + content type, or `null` when absent.
- `Exists(address) -> bool`
- `Delete(address)`

Address layout is a sharded prefix to avoid one flat directory: `ab/cd/abcd...` (64 lowercase hex).

## Backend choice

**MinIO** (S3-compatible object store) via `AddContainer` in both the AppHost and the test framework.
SeaweedFS was the initial selection but was rejected because its S3 gateway requires a JSON credentials
file and an admin JWT to create buckets — impractical to automate inside an Aspire dev/test container.
MinIO takes fixed environment credentials (`MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD`) and is the worktask's
approved fallback. See the ADR under `.docs/adr/`.

## How to swap backends

Implement `IBlobStorage` and register it in `DependencyInjection.AddBlobStorage`. Callers depend on the
abstraction only, so the S3 implementation can be replaced without touching Application or Host code.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-08-30 | Created — blob storage abstraction + MinIO S3 implementation, content addressing, gzip compression, lazy bucket creation. | — |
