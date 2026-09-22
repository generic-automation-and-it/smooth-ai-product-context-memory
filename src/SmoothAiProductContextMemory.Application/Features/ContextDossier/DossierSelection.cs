using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Retrieval;

namespace SmoothAiProductContextMemory.Application.Features.ContextDossier;

/// <summary>
/// The anchor set for a dossier read. Values within a category are alternatives; categories are
/// conjunctive (HLD-005 LADR-03 / BR-18). <see cref="WidenDepth"/> is always supplied — an export's
/// reach decides its cost and completeness claim, so it is never defaulted.
/// </summary>
public sealed record DossierAnchor(
    string? Repo,
    string? InitiativeName,
    TicketIdentity? Ticket,
    IReadOnlyList<string> Tags,
    string? Kind,
    string? Status,
    string? ScopeDimension,
    bool IncludeHistory,
    DateTimeOffset? AsOf,
    int WidenDepth,
    int ItemLimit);

/// <summary>An ordered selected claim, with the reason(s) it was added (BR-19).</summary>
public sealed record SelectedClaim(CheapMemory Memory, IReadOnlyList<string> ReachedVia);

public sealed record DossierSelectionResult(
    IReadOnlyList<SelectedClaim> Selected,
    int AnchorCount,
    int WidenedCount,
    int EdgeCount,
    bool DepthLimitReached,
    bool HiddenPathDropped,
    bool LimitReached);

/// <summary>
/// The deterministic selection half of a dossier read (HLD-005 LADR-03): resolve the anchor set
/// relationally, widen over the graph bounded by <see cref="DossierAnchor.WidenDepth"/>, scope-gate
/// every vertex with the hidden-dimension set, collapse the mechanically identical, and order
/// deterministically. Never writes; never hydrates a body.
/// </summary>
public static class DossierSelection
{
    /// <summary>Business-time validity, then capture time, then memory identity (LADR-07).</summary>
    internal static int CompareByProvenance(CheapMemory a, CheapMemory b)
    {
        int validity = a.ValidFrom.CompareTo(b.ValidFrom);
        if (validity != 0)
        {
            return validity;
        }

        int captured = a.CreatedOn.CompareTo(b.CreatedOn);
        if (captured != 0)
        {
            return captured;
        }

        return a.Uuid.CompareTo(b.Uuid);
    }

    public static async Task<DossierSelectionResult> ResolveAsync(
        IMemorySearch search,
        IMemoryTraversal traversal,
        ITicketGraph ticketGraph,
        IMemoryGraph graph,
        DossierAnchor anchor,
        CancellationToken cancellationToken)
    {
        bool hasGroupContext = false;
        MemoryScopeFilter.ScopeFilterPlan scopePlan =
            MemoryScopeFilter.Plan(anchor.ScopeDimension, hasGroupContext);
        IReadOnlyList<string> hiddenDimensions =
            MemoryScopeFilter.HiddenDimensions(anchor.ScopeDimension, hasGroupContext);

        // Anchor resolution: repo/initiative/tags/kind/status through the search abstraction in one
        // indexed statement; the ticket through the accepted ITicketGraph separation (mirrors
        // FindTicketPaths), which grants no scope consent from ticket identity (HLD-003 LADR-08).
        MemorySearchCriteria criteria = new()
        {
            Repo = Blank(anchor.Repo),
            InitiativeName = Blank(anchor.InitiativeName),
            Tags = anchor.Tags,
            Kind = Blank(anchor.Kind),
            Status = Blank(anchor.Status),
            RequiredScopeDimension = scopePlan.RequiredDimension,
            ExcludedScopeDimensions = scopePlan.ExcludedDimensions,
            CurrentOnly = true,
            AsOf = anchor.AsOf,
            Limit = MemorySearchDefaults.MaxLimit,
        };

        IReadOnlyList<CheapMemory> matched = await search.SearchAsync(criteria, cancellationToken);

        HashSet<Guid> anchorUuids = new(matched.Select(m => m.Uuid));
        var reachedVia = new Dictionary<Guid, List<string>>();
        foreach (CheapMemory m in matched)
        {
            reachedVia[m.Uuid] = ["anchor"];
        }

        if (anchor.Ticket is { } ticket)
        {
            TicketTraversalResult ticketResult = await ticketGraph.TraverseAsync(
                new TicketTraversalQuery
                {
                    Anchor = ticket,
                    MaxDepth = anchor.WidenDepth,
                    RequiredScopeDimension = scopePlan.RequiredDimension,
                    HiddenDimensions = hiddenDimensions,
                    Kind = Blank(anchor.Kind),
                },
                cancellationToken);

            // Conjunctive across categories: a ticket-selected memory must also satisfy the search
            // criteria. A no-match is a no-match — never broaden to return something.
            var ticketUuids = new HashSet<Guid>(ticketResult.Items.Select(i => i.Uuid));
            anchorUuids.RemoveWhere(u => !ticketUuids.Contains(u));
        }

        // No-match stays a no-match: do not broaden the criteria to produce a result.
        if (anchorUuids.Count == 0)
        {
            return new DossierSelectionResult(
                Selected: [],
                AnchorCount: 0,
                WidenedCount: 0,
                EdgeCount: 0,
                DepthLimitReached: false,
                HiddenPathDropped: false,
                LimitReached: false);
        }

        // Widening over the graph from the resolved anchor identities, bounded by WidenDepth,
        // scope-gated at every vertex with the hidden-dimension set.
        MemoryWidenResult widened = await traversal.WidenAsync(
            new MemoryWidenQuery
            {
                SourceUuids = [.. anchorUuids],
                MaxDepth = anchor.WidenDepth,
                RequiredScopeDimension = scopePlan.RequiredDimension,
                HiddenDimensions = hiddenDimensions,
                Kind = Blank(anchor.Kind),
                Status = Blank(anchor.Status),
                Limit = MemorySearchDefaults.MaxLimit,
            },
            cancellationToken);

        foreach (CheapMemory reached in widened.Memories)
        {
            if (!reachedVia.ContainsKey(reached.Uuid))
            {
                reachedVia[reached.Uuid] = [];
            }

            reachedVia[reached.Uuid].Add("widen");
        }

        // Mechanical collapse: one memory reached by several anchor/widening paths appears once. The
        // union of anchor matches (which may be isolated) and widened reaches is the selected set.
        Dictionary<Guid, CheapMemory> byUuid = new();
        foreach (CheapMemory m in matched)
        {
            byUuid[m.Uuid] = m;
        }

        foreach (CheapMemory m in widened.Memories)
        {
            byUuid.TryAdd(m.Uuid, m);
        }

        SelectedClaim[] selected =
        [
            .. byUuid.Values
                .Select(m => new SelectedClaim(m, reachedVia.TryGetValue(m.Uuid, out List<string>? v) ? v : []))
                .OrderBy(c => c.Memory, DossierSelectionCompare.Instance)
        ];

        int edgeCount = await CountEdgesAsync(graph, selected, cancellationToken);

        return new DossierSelectionResult(
            selected,
            AnchorCount: anchorUuids.Count,
            WidenedCount: widened.Memories.Count,
            EdgeCount: edgeCount,
            DepthLimitReached: widened.DepthLimitReached,
            HiddenPathDropped: widened.HiddenPathDropped,
            LimitReached: widened.LimitReached);
    }

    private static async Task<int> CountEdgesAsync(
        IMemoryGraph graph,
        IReadOnlyList<SelectedClaim> selected,
        CancellationToken cancellationToken)
    {
        if (selected.Count == 0)
        {
            return 0;
        }

        var uuids = new HashSet<Guid>(selected.Select(c => c.Memory.Uuid));
        IReadOnlyList<MemoryRelationship> all = await graph.ListAllAsync(cancellationToken);
        return all.Count(e => uuids.Contains(e.SourceUuid) && uuids.Contains(e.TargetUuid));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Build the anchor set from the wire fields shared by the bundle and preview requests.</summary>
    public static DossierAnchor AnchorFrom(
        string? repo,
        string? initiativeName,
        string? ticketProvider,
        string? ticketKey,
        IReadOnlyList<string>? tags,
        string? kind,
        string? status,
        string? scopeDimension,
        bool includeHistory,
        DateTimeOffset? asOf,
        int widenDepth)
    {
        TicketIdentity? ticket = null;
        if (!string.IsNullOrWhiteSpace(ticketProvider) && !string.IsNullOrWhiteSpace(ticketKey))
        {
            ticket = new TicketIdentity(ticketProvider, ticketKey);
        }

        // Tags are recorded deterministically (ordinal), so reordering the request's tags changes
        // nothing in the response — including the recorded effective selection (NFR-02).
        IReadOnlyList<string> sortedTags = [.. (tags ?? []).OrderBy(t => t, StringComparer.Ordinal)];

        return new DossierAnchor(
            repo,
            initiativeName,
            ticket,
            sortedTags,
            kind,
            status,
            scopeDimension,
            includeHistory,
            asOf,
            widenDepth,
            DossierDefaults.ItemLimit);
    }

    /// <summary>The recorded effective selection, in a form sufficient to repeat it (BR-20).</summary>
    public static DossierSelectionPlan BuildPlan(DossierAnchor anchor) =>
        new(
            anchor.Repo,
            anchor.InitiativeName,
            anchor.Ticket?.Provider,
            anchor.Ticket?.Key,
            anchor.Tags,
            anchor.Kind,
            anchor.Status,
            anchor.ScopeDimension,
            anchor.IncludeHistory,
            anchor.AsOf,
            anchor.WidenDepth,
            DossierCombinationRule.Value,
            anchor.IncludeHistory ? "included" : "current-only",
            "current-only, proposed-excluded unless status requested");

    private sealed class DossierSelectionCompare : IComparer<CheapMemory>
    {
        public static readonly DossierSelectionCompare Instance = new();

        public int Compare(CheapMemory? a, CheapMemory? b)
        {
            if (ReferenceEquals(a, b))
            {
                return 0;
            }

            if (a is null)
            {
                return -1;
            }

            if (b is null)
            {
                return 1;
            }

            return CompareByProvenance(a, b);
        }
    }
}
