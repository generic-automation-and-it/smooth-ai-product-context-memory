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
- **The MinIO client is handed an `IHttpClientFactory` client** (`BlobStorageOptions.HttpClientName`),
  never one it builds itself. That is what puts object-store calls under the Host's
  `ConfigureHttpClientDefaults` — standard resilience and service discovery — and under
  `HttpClient` trace instrumentation, so a blob call attaches to the request's trace instead of
  appearing as unrelated activity. The `Polly` meter reporting for blob calls is how you can tell
  the handler is actually attached.
- **Bucket created lazily** on first write (`EnsureBucketAsync`); reads treat a missing bucket as a
  missing object. No separate init container required.
- **Options bound from configuration**, populated by Aspire in development (`BlobStorage:Endpoint`,
  `BlobStorage:AccessKey`, `BlobStorage:SecretKey`, `BlobStorage:Bucket`). Validated on start.
- **Never log content or credentials.**

## Contract

- `Store(content, contentType?) -> address` — writes and returns the content address.
- `Get(address) -> BlobContent?` — returns content (decompressed) + content type, or `null` when absent.
- `Exists(address) -> bool`

**Deliberately no delete on the abstraction.** Content addresses are shared by identical bytes, so no
application code can prove an object is unreferenced across all versions; deletion awaits a separately
approved garbage-collection design informed by HLD-006 orphan accounting (which reports, never
deletes). The concrete `S3BlobStorage.DeleteAsync` exists solely for isolated test cleanup, and the L0
guard `BlobStorageCapabilityGuardTests` pins the interface surface to the three members above.

Address layout is a sharded prefix to avoid one flat directory: `ab/cd/abcd...` (64 lowercase hex).

## Backend choice

**MinIO** (S3-compatible object store) via `AddContainer` in both the AppHost and the test framework.
SeaweedFS was the initial selection but was rejected because its S3 gateway requires a JSON credentials
file and an admin JWT to create buckets — impractical to automate inside an Aspire dev/test container.
MinIO takes fixed environment credentials (`MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD`) and is the worktask's
approved fallback. See [HLD 001 LADR-06](../../../docs/hlds/001-context-memory-storage/ladrs/LADR-06-content-addressed-blob-storage.md).

## How to swap backends

Implement `IBlobStorage` and register it in `DependencyInjection.AddBlobStorage`. Callers depend on the
abstraction only, so the S3 implementation can be replaced without touching Application or Host code.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-16 | Deletion removed from `IBlobStorage`: normal application code structurally cannot delete objects; concrete adapter keeps a documented test-only delete. Guarded by `BlobStorageCapabilityGuardTests`. | HLD-001 AGENTS.md migration plans |
| 2026-09-13 | `.docs`→`docs` move recorded — the HLD 001 LADR-06 reference path now resolves under the visible `docs/hlds/` tree. | — |
| 2026-08-30 | Created — blob storage abstraction + MinIO S3 implementation, content addressing, gzip compression, lazy bucket creation. | — |
| 2026-09-27 | Archive integrity, three related corrections. (1) `SnapshotFormat.Version` 1→2: `DanglingReferences`/`MismatchedBodies` carry refusal semantics, so a v1 archive (field absent → 0) could still verify clean with a dangling body — a v1 archive is now refused by the gate rather than misread. **Existing v1 archives require re-capture.** (2) `VerifyAsync` also catches `InvalidDataException`, which `Deserialize<T>` raises for a literal `null` member and is not a `JsonException`, so a malformed manifest produced a raw stack trace instead of a finding. (3) `WriteAsync` writes to a temp path and moves on success, so a mid-write failure leaves no truncated archive at the destination; the move is an atomic replace, not a refusal to overwrite. | PR #118 review |
| 2026-09-27 | Removed three dead `ISnapshotArchive` members while editing the interface: `ReadBlobAsync` (0 callers, and its doc comment pointed callers at it while `RestoreArchive` used `SnapshotArchive.ReadBlob`), the `ReadEntryAsync` record component (0 callers), and `ReadCaptureAsync` (production-uncalled since `ReadAsync` began carrying the capture; its two test call sites now exercise the same gate through `ReadAsync`). Also corrected the write-path comment, which described an atomic *replace* as an "overwrite guard" — refusing a non-empty destination is a different guarantee and lives in the Markdown export sink. | PR review |
