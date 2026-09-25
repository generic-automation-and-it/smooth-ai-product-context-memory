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
    /// </summary>
    Task<SnapshotWriteReport> WriteAsync(
        string destinationPath,
        SnapshotCapture capture,
        SnapshotWalkResult walk,
        Func<string, Task<byte[]>> readBlobAsync,
        CancellationToken cancellationToken);

    /// <summary>Opens an archive for reading, returning its manifest and entry access.</summary>
    Task<SnapshotArchive> ReadAsync(string archivePath, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies an archive using only the archive: recomputes every entry hash, reconciles the
    /// manifest counts, and re-checks each blob body hash against both its manifest entry and the
    /// database-cited address. No service, database, object store or network. A clean archive yields
    /// zero findings; any truncated, altered or missing entry is detected and named.
    /// </summary>
    Task<SnapshotVerification> VerifyAsync(string archivePath, CancellationToken cancellationToken);

    /// <summary>
    /// Deserializes an archive's relational + graph capture back into a <see cref="SnapshotCapture"/>
    /// so it can be restored. Blob bodies are not returned here — the caller reads them separately
    /// via <see cref="ReadBlobAsync"/> when re-checking references against the archive.
    /// </summary>
    Task<SnapshotCapture> ReadCaptureAsync(string archivePath, CancellationToken cancellationToken);

    /// <summary>Reads one blob body from the archive by its content address.</summary>
    Task<byte[]> ReadBlobAsync(string archivePath, string address, CancellationToken cancellationToken);
}

/// <summary>Outcome of an archive write. Counts, the destination, and the report's key numbers.</summary>
public sealed record SnapshotWriteReport(
    string DestinationPath,
    SnapshotCounts Counts,
    int DanglingReferences,
    int UnreferencedObjects,
    int MismatchedBodies);

/// <summary>
/// An opened archive: the manifest, the actual archive member names, and accessors for reading entry
/// bytes either by member name or by blob content address. The blob accessors encapsulate the
/// archive's blob entry-name convention so callers never see the <c>blobs/</c> prefix.
/// </summary>
public sealed record SnapshotArchive(
    SnapshotManifest Manifest,
    IReadOnlyList<string> EntryNames,
    Func<string, Task<byte[]>> ReadEntryAsync,
    Func<string, bool> ContainsBlob,
    Func<string, byte[]> ReadBlob);
