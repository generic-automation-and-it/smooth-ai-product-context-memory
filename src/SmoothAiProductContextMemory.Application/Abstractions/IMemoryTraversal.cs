using SmoothAiProductContextMemory.Application.Common.Models;

namespace SmoothAiProductContextMemory.Application.Abstractions;

/// <summary>
/// Bounded multi-hop traversal over <c>:LINKS</c> edges — provenance reconstruction, not analytics.
/// </summary>
/// <remarks>
/// Lives in Infrastructure because the traversal and the descriptive fields must be produced by
/// <em>one</em> statement: the graph returns identities, and what those memories say comes from the
/// relational rows joined in the same SQL. Composing the two client-side would forfeit the reason an
/// in-database extension was chosen over a separate graph server (LADR-01).
/// </remarks>
public interface IMemoryTraversal
{
    Task<IReadOnlyList<MemoryPath>> FindPathsAsync(MemoryPathQuery query, CancellationToken cancellationToken);
}

public enum TraversalDirection
{
    /// <summary>Follow edges away from the source — <c>source depends_on …</c>.</summary>
    Outbound,

    /// <summary>Follow edges into the source. Depth 1 is the reverse lookup the store has always had.</summary>
    Inbound,

    /// <summary>Ignore direction. Reaches more for the same bound, so it costs more.</summary>
    Either,
}

/// <summary>
/// A fully resolved traversal request. The handler resolves the scope rule before constructing this,
/// so the provider only translates predicates.
/// </summary>
public sealed record MemoryPathQuery
{
    public required Guid SourceUuid { get; init; }

    /// <summary>Null walks to every memory reachable within the bound rather than to one endpoint.</summary>
    public Guid? TargetUuid { get; init; }

    /// <summary>
    /// Hop bound, always supplied. <c>required</c> rather than defaulted because nothing in the
    /// storage layer stops an unbounded path query, so forgetting the bound must not compile
    /// (LADR-07).
    /// </summary>
    public required int MaxDepth { get; init; }

    /// <summary>Restricts every hop to one relation. Null traverses any relation.</summary>
    public string? Relation { get; init; }

    public TraversalDirection Direction { get; init; } = TraversalDirection.Outbound;

    /// <summary>Relational predicate on the endpoint memory's current version.</summary>
    public string? Kind { get; init; }

    public string? Status { get; init; }

    /// <summary>Only endpoints in groups with this scope dimension. From the scope plan, never raw input.</summary>
    public string? RequiredScopeDimension { get; init; }

    /// <summary>Scope dimensions that must not be returned. From the scope plan.</summary>
    public IReadOnlyList<string> ExcludedScopeDimensions { get; init; } = [];

    public int Limit { get; init; } = MemorySearchDefaults.Limit;
}

/// <summary>One hop of a path, carrying the reason BR-11 requires an edge to record.</summary>
public sealed record MemoryPathHop(Guid SourceUuid, Guid TargetUuid, string Relation, string Reason);

/// <summary>
/// One path from the query's source. <paramref name="Endpoint"/> is the relational row for the last
/// vertex, selected in the same statement as the traversal.
/// </summary>
public sealed record MemoryPath(int Depth, IReadOnlyList<MemoryPathHop> Hops, CheapMemory Endpoint);

public static class MemoryTraversalDefaults
{
    /// <summary>
    /// The ceiling on <see cref="MemoryPathQuery.MaxDepth"/>. Deliberate, not measured: NFR-02 targets
    /// depth three and the headroom covers a longer provenance chain. Raising it requires re-measuring,
    /// because both the plan and the p95 are depth-sensitive.
    /// </summary>
    public const int MaxDepth = 5;
}
