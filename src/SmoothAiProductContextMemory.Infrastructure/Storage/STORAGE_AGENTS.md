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
| 2026-10-01 | Blob `ContentType` is validated on **verify and restore** (`SnapshotContentTypes.IsAllowed`): it lives only in the unhashed manifest and restore writes it verbatim as the object's `Content-Type`. Accepted: absent, or `text/plain` / `application/octet-stream` (what `SetMemories` and the S3 adapter's default write) with at most `charset=utf-8`; anything malformed or else is a `Corruption` finding, and restore refuses it (exit 2) before any body is stored. The value is never echoed into the finding. L0: `TarSnapshotArchiveTests` content-type theories, `RestoreArchiveHandlerTests`. | HLD-006 LADR-02, NFR-01 |
| 2026-10-01 | `TryReadEntries` classifies `UnauthorizedAccessException` as `SnapshotFindingKind.Unreadable`, no longer `Corruption`: a permissions refusal proves nothing about the archive's bytes, and restore maps `Corruption` to the integrity exit code. L0: `Verify_Reports_AnArchiveItCannotOpen_AsUnreadable_NotCorruption`. | HLD-006 NFR-01 |
| 2026-09-28 | **The archive's two durability gaps closed, and `verify`'s no-throw contract is now true of the container as well as the members.** `VerifyAsync` called `ReadEntries` directly and that method had no exception handling at all, so a truncated archive escaped as `EndOfStreamException`, a non-tar file the same, and a wrong path as `FileNotFoundException` — while the interface already promised that "any truncated, altered or missing entry is detected and named", and every existing tamper test built a *well-formed* tar and mutated a member, so the container was never exercised. A `TryReadEntries` wrapper now converts an unreadable container into a `Corruption` finding; the exception's message is deliberately not carried, because the I/O ones embed the full archive path and a finding is a value rendered in a report, so the *type* is kept and the path is not. `ReadAsync` still throws on the same inputs, since a restore must refuse rather than restore half an archive. Separately, the writer's temp-then-move was atomic but neither durable nor tidy: no `try`/`finally`, so a failed or cancelled write left a content-bearing `<dest>.tmp` holding a second copy of the corpus that nothing cleaned, and no flush before the rename, so a crash in between leaves a correctly-named archive holding none of its content. The writer now flushes to disk before the move and deletes the temp path in a `finally` (a no-op on success, since the move consumed it), swallowing a cleanup failure so it cannot mask the cause. Four new L0 cases drive a truncated member, a non-tar file, an absent path and an empty file; two more assert the temp file is gone after both a failed and a successful write. Both behaviours are mutation-verified. One trap worth recording: a first attempt truncated 64 bytes off the end and the archive verified **clean** — correctly, because the cut landed in tar's trailing zero blocks and every member was intact. The test that actually exercises truncation cuts inside the final member's body, located by walking the real headers. | pre-MVP review |
| 2026-09-27 | Archive format `SnapshotFormat.Version` 2→3: `SnapshotArchiveEntry` carries `ContentType` for every blob body, and restore passes it to the object store. A MIME type cannot be re-derived from content, so the loss begins at capture, and a pre-v3 archive (field absent → null) would restore every body as `application/octet-stream` — it is refused at `TarSnapshotArchive` rather than misread. **Existing v2 archives require re-capture.** | PR review |
| 2026-09-16 | Deletion removed from `IBlobStorage`: normal application code structurally cannot delete objects; concrete adapter keeps a documented test-only delete. Guarded by `BlobStorageCapabilityGuardTests`. | HLD-001 AGENTS.md migration plans |
| 2026-09-13 | `.docs`→`docs` move recorded — the HLD 001 LADR-06 reference path now resolves under the visible `docs/hlds/` tree. | — |
| 2026-08-30 | Created — blob storage abstraction + MinIO S3 implementation, content addressing, gzip compression, lazy bucket creation. | — |
| 2026-09-27 | Archive integrity, three related corrections. (1) `SnapshotFormat.Version` 1→2: `DanglingReferences`/`MismatchedBodies` carry refusal semantics, so a v1 archive (field absent → 0) could still verify clean with a dangling body — a v1 archive is now refused by the gate rather than misread. **Existing v1 archives require re-capture.** (2) `VerifyAsync` also catches `InvalidDataException`, which `Deserialize<T>` raises for a literal `null` member and is not a `JsonException`, so a malformed manifest produced a raw stack trace instead of a finding. (3) `WriteAsync` writes to a temp path and moves on success, so a mid-write failure leaves no truncated archive at the destination; the move is an atomic replace, not a refusal to overwrite. | PR #118 review |
| 2026-09-27 | Removed two dead `ISnapshotArchive` members while editing the interface: `ReadBlobAsync` (0 callers, and its doc comment pointed callers at it while `RestoreArchive` used `SnapshotArchive.ReadBlob`) and the `ReadEntryAsync` record component (0 callers). `ReadCaptureAsync` was **left in place**: it is still declared, still implemented, and still exercised by three L0 tests, and with `SnapshotArchive.Capture` now on the opened record a future pass can drop it once those tests read `ReadAsync(...).Capture`. Also corrected the write-path comment, which described an atomic *replace* as an "overwrite guard" — refusing a non-empty destination is a different guarantee and lives in the Markdown export sink. | PR review |
| 2026-09-27 | Dropped the remaining dead `ISnapshotArchive` member `ReadCaptureAsync` (0 production callers). The capture is `SnapshotArchive.Capture` on the record that `ReadAsync` returns, and both paths went through the same `ReadAndGateManifest` + `ReadCapture`, so the three L0 tests now read `(await ReadAsync(...)).Capture`; the format-gate test asserts through `ReadAsync` directly. | PR #120 review |
| 2026-09-30 | **Verify now checks that every body the capture cites is in the archive, and two null manifest shapes are findings rather than stack traces.** (1) Every other verify check works from the manifest, and the manifest is the one member nothing hash-declares — so a body could be dropped from the archive, its manifest line deleted, the object count adjusted and the dangling-reference counter zeroed, and verify reported **clean**; the restore then refused the same archive, because the capture still cited an absent body. `CheckCitedBodiesPresent` reads the `memory_version` member's own cited addresses (raw JSON, not the bound model, for the reason `CheckCount` does the same) and reports each absent one. A genuine capture-time dangling reference is still counted in the manifest and still reported; this is the independent check the counter cannot be. (2) A JSON `null` element in the manifest's entry list and an entry with no `name` both reached a `Dictionary` and left verify throwing `ArgumentNullException` — neither shape is one the writer produces, which is exactly why the archive's own bytes cannot be assumed to. Both are now findings, and the mutation check removes all three guards at once and fails all three tests. | HLD-006 NFR-02, LADR-02 |
