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

/// <summary>One archive member: its logical name inside the archive and its content hash.</summary>
public sealed record SnapshotArchiveEntry(string Name, string Sha256, long Size);

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
    public const int Version = 1;
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
