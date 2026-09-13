namespace SmoothAiProductContextMemory.Application.Abstractions;

/// <summary>
/// Relationship store. AGE Cypher lives in Infrastructure; handlers never issue graph SQL.
/// There is no delete operation — edge removal is <c>BEFORE DELETE ON memory</c> only.
/// </summary>
public interface IMemoryGraph
{
    Task<bool> ExistsAsync(Guid sourceUuid, Guid targetUuid, string relation, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the directed edge when the triple is absent. Returns <see langword="false"/> on
    /// duplicate — callers map that to skip (batch) or 409 (standalone). Never throws for a duplicate.
    /// </summary>
    Task<bool> CreateAsync(
        Guid sourceUuid,
        Guid targetUuid,
        string relation,
        string reason,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MemoryRelationship>> ListAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MemoryRelationship>> ListTouchingAsync(Guid uuid, CancellationToken cancellationToken);
}

public sealed record MemoryRelationship(Guid SourceUuid, Guid TargetUuid, string Relation, string Reason);
