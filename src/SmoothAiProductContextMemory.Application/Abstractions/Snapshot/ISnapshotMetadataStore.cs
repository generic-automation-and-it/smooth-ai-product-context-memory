namespace SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

/// <summary>
/// Persisted summary of the most recent snapshot so <see cref="Features.Snapshot.SnapshotPreflight"/>
/// can report raw voluminous numbers (corpus counts, orphan/dangling) from the last walk without
/// re-running a full, expensive walk per request, and can state how old the last snapshot is
/// (LADR-04). Content-free — counts, identifiers and timestamps only.
/// </summary>
public interface ISnapshotMetadataStore
{
    Task<SnapshotMetadata?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(SnapshotMetadata metadata, CancellationToken cancellationToken = default);
}

/// <summary>The last snapshot's summary. <see cref="SnapshotRecency"/> text is derived by the caller.</summary>
public sealed record SnapshotMetadata(
    DateTimeOffset LastSnapshotAt,
    SnapshotCounts Counts,
    int DanglingReferences,
    int UnreferencedObjects);
