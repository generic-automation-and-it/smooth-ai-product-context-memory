using System.Net.Http.Headers;

namespace SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

/// <summary>
/// The archive's self-verifying manifest. One entry per archive member with its content hash,
/// plus corpus-level counts and a statement of what is deliberately excluded. Deliberately
/// carries no generation timestamp inside the hashed content — the snapshot date lives in archive
/// metadata (LADR-02). Counts are what the restore reconciliation closes against (LADR-05).
/// </summary>
/// <remarks>
/// <see cref="DanglingReferences"/> and <see cref="MismatchedBodies"/> record the capture-time
/// defects that were surfaced but not faithfully archived: a dangling reference has no body in
/// the archive, so a restore of this archive must fail on it, and verify must therefore not
/// report it clean. Without these counts an archive that downloaded a store with an unresolvable
/// reference would verify clean and then fail restore (LADR-02).
/// </remarks>
public sealed record SnapshotManifest(
    int FormatVersion,
    IReadOnlyList<SnapshotArchiveEntry> Entries,
    SnapshotCounts Counts,
    SnapshotExclusions Exclusions,
    int DanglingReferences = 0,
    int MismatchedBodies = 0);

/// <summary>
/// One archive member: its logical name inside the archive and its content hash. Blob bodies also
/// carry their content type, because a restore cannot re-derive a MIME type from content — the loss
/// would start at capture if the field were absent from the archive (HLD-006).
/// </summary>
public sealed record SnapshotArchiveEntry(string Name, string Sha256, long Size, string? ContentType = null);

/// <summary>
/// The blob content types an archive may carry. <see cref="SnapshotArchiveEntry.ContentType"/> sits in
/// the manifest, the one member nothing hash-declares, and restore hands it to the object store as the
/// stored object's <c>Content-Type</c> — so it is untrusted input. Only what this system writes is
/// accepted: the write path stores <c>text/plain; charset=utf-8</c>, and the object store defaults an
/// absent type to <c>application/octet-stream</c>. Anything else is a manifest the writer never produced.
/// </summary>
public static class SnapshotContentTypes
{
    private static readonly string[] AllowedMediaTypes = ["text/plain", "application/octet-stream"];

    /// <summary>True for an absent type, or a well-formed allowlisted type with at most a UTF-8 charset.</summary>
    public static bool IsAllowed(string? contentType)
    {
        if (contentType is null)
        {
            return true;
        }

        if (!MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed)
            || parsed.MediaType is not { } mediaType
            || !AllowedMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return parsed.Parameters.All(static p =>
            string.Equals(p.Name, "charset", StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Corpus-level counts, each of which the restore reconciliation must reproduce exactly.
/// <c>Objects</c> is the number of referenced blob bodies captured.
/// </summary>
public sealed record SnapshotCounts(
    int Memories,
    int Versions,
    int Vertices,
    int Edges,
    int Objects,
    int TicketVertices,
    int TicketEdges);

/// <summary>What is deliberately absent from the archive, stated rather than reported as loss.</summary>
public sealed record SnapshotExclusions(IReadOnlyList<string> Items);

/// <summary>Archive format version. Bump on any breaking change to the member layout.</summary>
public static class SnapshotFormat
{
    // v2: added DanglingReferences/MismatchedBodies with refusal semantics — an archive that
    // recorded a dangling or mismatched body at capture now verifies non-clean, so archives written
    // before the field existed (v1, field absent→0) can no longer be certified restorable.
    // v3: SnapshotArchiveEntry carries ContentType for blob bodies. A restore cannot re-derive a
    // body's MIME type from content, so the loss begins at capture; archives written before the
    // field existed (v2, field absent→null) would restore bodies as application/octet-stream, so
    // they carry the same refusal semantics as v2.
    public const int Version = 3;
}

/// <summary>Canonical logical names for archive members that are not blob bodies.</summary>
public static class SnapshotEntryNames
{
    public const string Manifest = "manifest.json";
    public const string Initiatives = "relational/initiative.json";
    public const string Labels = "relational/label.json";
    public const string MemoryGroups = "relational/memory_group.json";
    public const string GroupDescriptions = "relational/group_description.json";
    public const string Memories = "relational/memory.json";
    public const string MemoryVersions = "relational/memory_version.json";
    public const string Vertices = "graph/vertices.json";
    public const string Edges = "graph/edges.json";
    public const string TicketVertices = "graph/ticket_vertices.json";
    public const string TicketEdges = "graph/ticket_edges.json";
}
