namespace SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

/// <summary>
/// Captures the relational + AGE graph state of both stores from one consistent snapshot, and
/// restores it into an empty target. Infrastructure supplies the implementation over the shared
/// <c>NpgsqlDataSource</c> and the AGE storage tables; Application orchestrates the archive.
/// </summary>
public interface ISnapshotRepository
{
    /// <summary>
    /// Reads all relational rows and graph vertices/edges from one consistent database snapshot, and
    /// resolves every blob the captured state cites through <see cref="IBlobStorage"/>. The caller
    /// supplies a connection string so capture and restore can target an explicit database (a restore
    /// runs while no API is serving, and owns its connections exclusively — LADR-07 / LADR-05).
    /// </summary>
    Task<SnapshotCaptureResult> CaptureAsync(
        string connectionString,
        CancellationToken cancellationToken);

    /// <summary>
    /// Restores the captured state into the target database, then returns counts for the
    /// reconciliation. Refuses a non-empty target unless <paramref name="overrideNonEmpty"/> is set
    /// (LADR-05). Restores relational rows, Memory/Ticket vertices, then LINKS/TICKET_PARENT edges,
    /// in one transaction, honouring the append-only triggers (SET LOCAL) and the AGE session rules.
    /// </summary>
    Task<RestoreResults> RestoreAsync(
        string connectionString,
        SnapshotCapture capture,
        bool overrideNonEmpty,
        CancellationToken cancellationToken);
}

/// <summary>Capture output plus the walk result, and the counts the manifest records.</summary>
public sealed record SnapshotCaptureResult(
    SnapshotCapture Capture,
    SnapshotWalkResult Walk,
    SnapshotCounts Counts);

/// <summary>Post-restore store counts and a bounded-traversal result, used to close the reconciliation.</summary>
public sealed record RestoreResults(
    int Memories,
    int Versions,
    int Vertices,
    int Edges,
    int Objects,
    int TicketVertices,
    int TicketEdges,
    int TraversalPathCount);
