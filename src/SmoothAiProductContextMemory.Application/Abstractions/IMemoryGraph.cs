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

    /// <summary>
    /// Lists the edges whose two endpoints are both in <paramref name="uuids"/>. Bounded by the size
    /// of the selection (index-served per uuid, HLD-003 LADR-06) rather than the whole edge set, so a
    /// read that needs only a selected subset does not scan every edge. Used by the dossier selection
    /// (HLD-005), whose bounded-cost bar is NFR-03.
    /// </summary>
    Task<IReadOnlyList<MemoryRelationship>> ListEdgesAsync(
        IReadOnlyCollection<Guid> uuids,
        CancellationToken cancellationToken);

    /// <summary>
    /// True when the memory already carries the <c>ABOUT</c> edge to the ticket.
    /// </summary>
    Task<bool> AboutExistsAsync(
        Guid memoryUuid,
        string provider,
        string key,
        CancellationToken cancellationToken);

    /// <summary>
    /// Declares <c>(Memory)-[:ABOUT]->(Ticket)</c> when absent. The Memory vertex is created on demand;
    /// the Ticket vertex must already exist (the group membership trigger owns it). Returns
    /// <see langword="false"/> on duplicate.
    /// </summary>
    Task<bool> CreateAboutAsync(
        Guid memoryUuid,
        string provider,
        string key,
        string reason,
        CancellationToken cancellationToken);
}

public sealed record MemoryRelationship(Guid SourceUuid, Guid TargetUuid, string Relation, string Reason);
