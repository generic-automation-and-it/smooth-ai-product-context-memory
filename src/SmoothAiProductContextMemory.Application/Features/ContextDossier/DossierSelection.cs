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
    bool LimitReached,
    IReadOnlyList<MemoryRelationship> Edges);

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
        // One filter for all three stages, so a predicate cannot reach one stage and miss another.
        SelectionFilter filter = SelectionFilter.From(anchor);

        // Anchor resolution: repo/initiative/tags/kind/status through the search abstraction in one
        // indexed statement; the ticket through the accepted ITicketGraph separation (mirrors
        // FindTicketPaths), which grants no scope consent from ticket identity (HLD-003 LADR-08).
        // A ticket anchor is traversed first so the selected uuids narrow the anchor search in SQL
        // before the row ceiling, rather than intersecting an already-truncated result set (HLD-005
        // NFR-03). Ticket identity grants no scope consent; the traversal is scope-gated separately.
        TicketTraversalResult? ticketResult = null;
        if (anchor.Ticket is { } ticket)
        {
            ticketResult = await ticketGraph.TraverseAsync(
                filter.TicketTraversal(ticket, anchor.WidenDepth),
                cancellationToken);
        }

        bool ticketSupplied = anchor.Ticket is { };
        HashSet<Guid>? ticketUuids = ticketResult is null
            ? null
            : new HashSet<Guid>(ticketResult.Items.Select(i => i.Uuid));

        // The ticket traversal's disclosure applies to every return below, the two no-match ones
        // included: a ticket walk that was cut short can leave the anchor search with nothing to
        // match, and that empty result is truncated, not complete.
        bool ticketDepthLimitReached = ticketResult?.Disclosure.DepthLimitReached ?? false;
        bool ticketLimitReached = (ticketResult?.Disclosure.MemoryLimitReached ?? false)
            || (ticketResult?.Disclosure.PathLimitReached ?? false);

        // A supplied ticket that resolves to no eligible identities (missing, hidden, or no eligible
        // memories) must stay a no-match; broadening to an unrestricted search would silently select
        // unrelated visible memories (R07 / H7). The traversal may have hit a depth/path/memory limit
        // even while yielding nothing — propagate its disclosure flags so a truncated-empty result is
        // not reported as complete-empty (H5).
        if (ticketSupplied && ticketUuids is { Count: 0 })
        {
            return new DossierSelectionResult(
                Selected: [],
                AnchorCount: 0,
                WidenedCount: 0,
                EdgeCount: 0,
                DepthLimitReached: ticketDepthLimitReached,
                HiddenPathDropped: false,
                LimitReached: ticketLimitReached,
                Edges: []);
        }

        MemorySearchCriteria criteria = filter.AnchorSearch(
            Blank(anchor.Repo),
            Blank(anchor.InitiativeName),
            anchor.Tags,
            ticketSupplied ? [.. ticketUuids!] : null);

        IReadOnlyList<CheapMemory> matched = await search.SearchAsync(criteria, cancellationToken);

        HashSet<Guid> anchorUuids = new(matched.Select(m => m.Uuid));
        var reachedVia = new Dictionary<Guid, List<string>>();
        foreach (CheapMemory m in matched)
        {
            reachedVia[m.Uuid] = ["anchor"];
        }

        // No-match stays a no-match: do not broaden the criteria to produce a result.
        if (anchorUuids.Count == 0)
        {
            return new DossierSelectionResult(
                Selected: [],
                AnchorCount: 0,
                WidenedCount: 0,
                EdgeCount: 0,
                DepthLimitReached: ticketDepthLimitReached,
                HiddenPathDropped: false,
                LimitReached: ticketLimitReached,
                Edges: []);
        }

        // Widening over the graph from the resolved anchor identities, bounded by WidenDepth,
        // scope-gated at every vertex with the hidden-dimension set.
        MemoryWidenResult widened = await traversal.WidenAsync(
            filter.Widen([.. anchorUuids], anchor.WidenDepth),
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

        IReadOnlyList<MemoryRelationship> edges = await LoadEdgesAsync(graph, selected, cancellationToken);

        bool depthLimitReached = widened.DepthLimitReached || ticketDepthLimitReached;
        bool limitReached = widened.LimitReached || ticketLimitReached;

        return new DossierSelectionResult(
            selected,
            AnchorCount: anchorUuids.Count,
            WidenedCount: widened.Memories.Count,
            EdgeCount: edges.Count,
            DepthLimitReached: depthLimitReached,
            HiddenPathDropped: widened.HiddenPathDropped,
            LimitReached: limitReached,
            Edges: edges);
    }

    private static async Task<IReadOnlyList<MemoryRelationship>> LoadEdgesAsync(
        IMemoryGraph graph,
        IReadOnlyList<SelectedClaim> selected,
        CancellationToken cancellationToken)
    {
        if (selected.Count == 0)
        {
            return [];
        }

        // Edges among the selected memories only, loaded once so the manifest count and the returned
        // edges come from one read (HLD-005 F6). A selected memory's relationship to a hidden one is
        // absent, because the hidden memory is never selected (NFR-01). The bound is the selection
        // size, not the whole edge table (HLD-005 NFR-03; index-served per HLD-003 LADR-06).
        var uuids = new HashSet<Guid>(selected.Select(c => c.Memory.Uuid));
        return await graph.ListEdgesAsync(uuids, cancellationToken);
    }

    internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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

    /// <summary>
    /// The limits a selection discloses, shared by the bundle and the preview (LADR-201) so the
    /// consent artefact and the distributed bundle report the same set. All of them are derivable from
    /// the selection alone — a depth bound hit, a fetch-ceiling hit (where the selection cannot know
    /// whether more matched), and a selection that filled or exceeded the stated ceiling
    /// (<see cref="DossierAnchor.ItemLimit"/>) even when no widen/ticket limit flag was raised, so a
    /// truncated-to-the-ceiling result is never presented as unbounded. The preview knows
    /// <c>Selected.Count</c>, so that last one is predictable by both slices. The history-inflated
    /// item cut the bundle makes but the preview cannot predict is disclosed by the bundle through its
    /// omitted list (per-item <see cref="DossierOmissionReason.CapReached"/>), never through a limit
    /// the preview was not shown.
    /// </summary>
    internal static IReadOnlyList<DossierLimitHit> BuildLimitsHit(
        DossierSelectionResult selection,
        DossierAnchor anchor)
    {
        var hits = new List<DossierLimitHit>();
        if (selection.DepthLimitReached)
        {
            hits.Add(new DossierLimitHit(DossierOmissionReason.DepthReached, anchor.WidenDepth));
        }

        // The anchor search fills the fetch ceiling without raising a widen/ticket limit flag, so
        // LimitReached alone misses a selection that was truncated by the ceiling. Selected.Count
        // reaching the ceiling is the conservative signal both slices can compute identically.
        if (selection.LimitReached || selection.Selected.Count >= anchor.ItemLimit)
        {
            hits.Add(new DossierLimitHit(DossierOmissionReason.CapReached, anchor.ItemLimit));
        }

        return [.. hits];
    }

    /// <summary>
    /// The recorded retrieval policy, derived from what the anchor actually selects rather than
    /// asserted as a fixed string.
    /// </summary>
    /// <remarks>
    /// This was the literal <c>"current-only, proposed-excluded unless status requested"</c>, which
    /// described an opt-in the ticket traversal did not have: the SQL hard-coded
    /// <c>status &lt;&gt; 'proposed'</c>, and the traversal query carried no status field, so
    /// <c>ticket + status=proposed</c> always returned <c>noMatch</c> while the manifest told a caller
    /// the opposite. A manifest is a record of the effective selection sufficient to repeat it
    /// (BR-20), so a clause naming a capability the request cannot exercise is a false record, not a
    /// conservative one. The opt-in now exists on the traversal, and this derives the clause from the
    /// anchor so the two cannot drift again.
    ///
    /// The clause stayed false a second time, for the same reason: widening was the one stage with no
    /// proposed rule whatsoever, so a proposed memory one hop from an approved anchor was selected
    /// under a manifest reading "proposed-excluded". Fixing the third stage is what makes this line
    /// true — a derived clause cannot be right while one of the three stages it summarises is wrong.
    /// All three stage queries are now built from one <see cref="SelectionFilter"/>, so the status and
    /// proposed rule this clause summarises is the one every stage applies.
    ///
    /// The blank test is <see cref="Blank"/>'s, the same one <see cref="ResolveAsync"/> applies, not a
    /// length test: a whitespace-only status matches nothing and therefore selects with
    /// proposed-excluded, so recording it as honoured would be the false record this method exists to
    /// prevent.
    /// </remarks>
    internal static string RetrievalPolicy(DossierAnchor anchor) =>
        Blank(anchor.Status) is { } status
            ? $"current-only, status={status}"
            : "current-only, proposed-excluded";

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
            RetrievalPolicy(anchor));

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
