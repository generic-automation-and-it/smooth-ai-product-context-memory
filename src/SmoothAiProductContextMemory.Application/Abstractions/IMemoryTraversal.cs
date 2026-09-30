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

    /// <summary>
    /// Widens from a <em>set</em> of source memory identities over the graph, bounded by depth,
    /// scope-gated at every vertex crossed. Returns the distinct reached memories (never the sources
    /// themselves — those are anchor-resolved separately) and whether a bound was reached.
    /// </summary>
    Task<MemoryWidenResult> WidenAsync(MemoryWidenQuery query, CancellationToken cancellationToken);
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

/// <summary>
/// A widening request over a <em>set</em> of source memory identities: every memory reachable from any
/// source within the depth bound, with the whole route dropped when it crosses a hidden-dimension
/// vertex. The handler resolves the scope rule before constructing this, so the provider only
/// translates predicates (HLD-005 LADR-03).
/// </summary>
public sealed record MemoryWidenQuery
{
    /// <summary>Anchor memory identities to widen from. Never empty.</summary>
    public required IReadOnlyList<Guid> SourceUuids { get; init; }

    /// <summary>
    /// Hop bound, always supplied. <c>required</c> rather than defaulted for the same reason as
    /// <see cref="MemoryPathQuery.MaxDepth"/>: an export's reach determines both its cost and its
    /// completeness claim, so the bound is the last place to inherit an unexamined number (LADR-03).
    /// </summary>
    public required int MaxDepth { get; init; }

    public TraversalDirection Direction { get; init; } = TraversalDirection.Outbound;

    /// <summary>Relational predicate on the reached memory's current version.</summary>
    public string? Kind { get; init; }

    /// <summary>Exact status match. When null, <see cref="ExcludeProposed"/> applies instead.</summary>
    public string? Status { get; init; }

    /// <summary>
    /// Whether a reached proposed memory is withheld. Ignored once <see cref="Status"/> is supplied,
    /// which is the same precedence <see cref="IMemorySearch.MemorySearchCriteria"/> and
    /// <see cref="ITicketGraph.TicketTraversalQuery"/> use.
    /// </summary>
    /// <remarks>
    /// Added because widening was the only one of the three dossier selection stages that carried no
    /// proposed rule at all — its SQL was <c>(@status IS NULL OR v.status = @status)</c>, which with a
    /// null status admits everything. So a proposed memory reachable from an approved anchor was
    /// selected while the manifest recorded <c>current-only, proposed-excluded</c>: the artefact a caller
    /// repeats the selection from asserted the opposite of what the selection did. The default is
    /// <c>true</c> so a caller that never considers the rule still gets it, which is what the other two
    /// stages' defaults were doing.
    /// </remarks>
    public bool ExcludeProposed { get; init; } = true;

    /// <summary>
    /// Business-time point the reached memory's current version must be valid at. Null means no
    /// window is applied.
    /// </summary>
    /// <remarks>
    /// Added because the dossier anchor search honoured <c>AsOf</c> while widening silently did not,
    /// so a manifest recorded an <c>AsOf</c> that only the first of the two selection stages honoured
    /// — and the second stage could add memories the first had excluded. The window predicate is the
    /// same one <see cref="IMemorySearch.MemorySearchCriteria.AsOf"/> applies, deliberately: this
    /// filters the <em>current</em> version by its validity window rather than reconstructing the
    /// version that was current at <c>AsOf</c>. The append-only trigger admits only an
    /// <c>is_current</c> flip, so historical reconstruction is not available here — see
    /// PERSISTENCE_AGENTS.md.
    /// </remarks>
    public DateTimeOffset? AsOf { get; init; }

    public string? RequiredScopeDimension { get; init; }

    /// <summary>
    /// Dimensions a path must not cross. From <see cref="MemoryScopeFilter.HiddenDimensions"/> — not
    /// the excluded set, which is empty for every explicit dimension. A path through a hidden memory is
    /// dropped whole, never shortened (HLD-005 NFR-01).
    /// </summary>
    public IReadOnlyList<string> HiddenDimensions { get; init; } = [];

    public int Limit { get; init; } = MemorySearchDefaults.Limit;
}

/// <summary>
/// The distinct memories reached by a widening, plus disclosure of which bounds were reached and whether
/// a path through a hidden memory was dropped. The sources themselves are not included — they are
/// anchor-resolved separately.
/// </summary>
public sealed record MemoryWidenResult(
    IReadOnlyList<CheapMemory> Memories,
    bool DepthLimitReached,
    bool LimitReached,
    bool HiddenPathDropped);
