namespace SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

/// <summary>
/// Writes and reads the corpus snapshot archive — a single tar containing the manifest plus one
/// entry per member (relational capture, graph capture, and each referenced blob body). The
/// manifest is content-addressed and self-verifying (LADR-02); the archive is read offline by
/// verify and restored from by the one-shot container. Implementation is storage-engine-agnostic
/// in form; nothing here leaks database or object-store concepts.
/// </summary>
public interface ISnapshotArchive
{
    /// <summary>
    /// Writes one archive to <paramref name="destinationPath"/> containing the relational capture,
    /// graph capture, every referenced blob body (read via <paramref name="readBlobAsync"/>), and the
    /// self-verifying manifest. A blob whose content hashes to a different address than the database
    /// cites is still recorded faithfully under that cited address (LADR-02) — it is a capture-time
    /// cross-store inconsistency to be surfaced, not a reason to skip the body. A dangling (unresolvable)
    /// body cannot be written. Copies are never deleted; accounting is reporting only (LADR-06).
    /// <see cref="ReadBlob"/> returns the body plus the SHA-256 already computed by the capture walk,
    /// so the writer does not hash every blob a second time to fill its manifest entry, and the
    /// content type captured at read time so the manifest entry can carry it. The token reaches every
    /// blob read and every member write; a cancelled write leaves no archive and no temp file behind.
    /// </summary>
    Task<SnapshotWriteReport> WriteAsync(
        string destinationPath,
        SnapshotCapture capture,
        SnapshotWalkResult walk,
        Func<string, CancellationToken, Task<(byte[] Content, string Sha256, string? ContentType)>> readBlobAsync,
        CancellationToken cancellationToken);

    /// <summary>Opens an archive for reading, returning its manifest and entry access.</summary>
    Task<SnapshotArchive> ReadAsync(string archivePath, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies an archive using only the archive: recomputes every entry hash, reconciles the
    /// manifest counts, and re-checks each blob body hash against both its manifest entry and the
    /// database-cited address. No service, database, object store or network. A clean archive yields
    /// zero findings; any truncated, altered or missing entry is detected and named.
    /// </summary>
    /// <remarks>
    /// This never throws for a defective or absent archive — including a truncated one, a file that
    /// is not a tar container, and a path that does not exist, all of which are reported as findings.
    /// That is the contract callers script against, and it is why the container read is wrapped
    /// separately from the member checks rather than being allowed to escape.
    /// <see cref="ReadAsync"/> deliberately differs: it throws on the same inputs, because a restore
    /// must refuse rather than restore half an archive.
    /// </remarks>
    Task<SnapshotVerification> VerifyAsync(string archivePath, CancellationToken cancellationToken);
}

/// <summary>Outcome of an archive write. Counts, the destination, and the report's key numbers.</summary>
public sealed record SnapshotWriteReport(
    string DestinationPath,
    SnapshotCounts Counts,
    int DanglingReferences,
    int UnreferencedObjects,
    int MismatchedBodies);

/// <summary>
/// An opened archive: the manifest, the actual archive member names, the already-deserialised
/// capture, and accessors for reading a blob body by its content address or testing for one.
/// The blob accessors encapsulate the archive's blob entry-name convention so callers never see the
/// <c>blobs/</c> prefix. The capture is carried here so a caller that needs both the capture and the
/// open archive (restore) does not read and materialise the tar twice.
/// </summary>
public sealed record SnapshotArchive(
    SnapshotManifest Manifest,
    IReadOnlyList<string> EntryNames,
    SnapshotCapture Capture,
    Func<string, bool> ContainsBlob,
    Func<string, byte[]> ReadBlob,
    Func<string, string?> BlobContentType);
