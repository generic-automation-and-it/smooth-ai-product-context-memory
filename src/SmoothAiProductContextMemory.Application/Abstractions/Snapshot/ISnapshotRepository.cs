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
    /// Whether the target database holds no corpus. Lets the caller refuse a non-empty target before
    /// writing anything to either store; <see cref="RestoreAsync"/> re-checks inside its transaction.
    /// </summary>
    Task<bool> IsTargetEmptyAsync(
        string connectionString,
        CancellationToken cancellationToken);

    /// <summary>
    /// Restores the captured state into the target database, reads every count back from the
    /// restored tables and graph labels inside the same transaction, and commits only when each
    /// read-back count equals <paramref name="expected"/> and the bounded traversal reaches every
    /// edge — otherwise it rolls back, so a partial restore is never committed (LADR-05 / NFR-02).
    /// Refuses a non-empty target unless <paramref name="overrideNonEmpty"/> is set.
    /// </summary>
    Task<RestoreResults> RestoreAsync(
        string connectionString,
        SnapshotCapture capture,
        SnapshotCounts expected,
        bool overrideNonEmpty,
        CancellationToken cancellationToken);
}

/// <summary>Capture output plus the walk result, and the counts the manifest records.</summary>
public sealed record SnapshotCaptureResult(
    SnapshotCapture Capture,
    SnapshotWalkResult Walk,
    SnapshotCounts Counts);

/// <summary>
/// Store counts read back from the restored database (not echoed from the archive) plus a
/// bounded-traversal result, used to close the reconciliation. <c>Committed</c> is false when the
/// read-back disagreed with the manifest and the restore was rolled back.
/// </summary>
public sealed record RestoreResults(
    int Memories,
    int Versions,
    int Vertices,
    int Edges,
    int TicketVertices,
    int TicketEdges,
    int TraversalPathCount,
    bool Committed);
