using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Retrieval;

namespace SmoothAiProductContextMemory.Application.Features.ContextDossier;

/// <summary>
/// The predicates every dossier selection stage applies to the memories it returns, built once from
/// the anchor and handed to all three stage queries (ticket traversal, anchor search, widening).
/// </summary>
/// <remarks>
/// <para>
/// Each stage used to be told the same rule separately, and each rule drifted once: <c>AsOf</c>
/// reached the anchor search but not widening, so widening could add a memory the anchor search had
/// excluded by validity window; the ticket status opt-in was missing, so the traversal's identity set
/// held no proposed memory and <c>ticket + status=proposed</c> always reported <c>noMatch</c>; and
/// widening carried no proposed rule at all, so a proposed memory one hop from an approved anchor
/// was selected while <see cref="DossierSelection.RetrievalPolicy"/> recorded
/// <c>proposed-excluded</c>. A stage query is now constructed only through this record, so a
/// predicate is added to the selection in one place.
/// </para>
/// <para>
/// <see cref="ExcludeProposed"/> is stated rather than inherited from each query's default. It is
/// superseded by <see cref="Status"/> once a status is supplied, which is the precedence all three
/// providers implement. The ticket traversal has no <c>AsOf</c>: its identities only narrow the
/// anchor search, which applies the window to them before anything is selected.
/// </para>
/// </remarks>
internal sealed record SelectionFilter(
    string? Kind,
    string? Status,
    bool ExcludeProposed,
    string? RequiredScopeDimension,
    IReadOnlyList<string> ExcludedScopeDimensions,
    IReadOnlyList<string> HiddenDimensions,
    DateTimeOffset? AsOf)
{
    public static SelectionFilter From(DossierAnchor anchor)
    {
        MemoryScopeFilter.ScopeFilterPlan scopePlan = MemoryScopeFilter.Plan(anchor.ScopeDimension, false);
        return new SelectionFilter(
            Kind: DossierSelection.Blank(anchor.Kind),
            Status: DossierSelection.Blank(anchor.Status),
            ExcludeProposed: true,
            RequiredScopeDimension: scopePlan.RequiredDimension,
            ExcludedScopeDimensions: scopePlan.ExcludedDimensions,
            HiddenDimensions: MemoryScopeFilter.HiddenDimensions(anchor.ScopeDimension, false),
            AsOf: anchor.AsOf);
    }

    public TicketTraversalQuery TicketTraversal(TicketIdentity ticket, int maxDepth) =>
        new()
        {
            Anchor = ticket,
            MaxDepth = maxDepth,
            RequiredScopeDimension = RequiredScopeDimension,
            HiddenDimensions = HiddenDimensions,
            Kind = Kind,
            Status = Status,
            ExcludeProposed = ExcludeProposed,
            PathLimit = MemorySearchDefaults.MaxLimit,
            MemoryLimit = MemorySearchDefaults.MaxLimit,
        };

    public MemorySearchCriteria AnchorSearch(
        string? repo,
        string? initiativeName,
        IReadOnlyList<string> tags,
        IReadOnlyList<Guid>? uuidFilter) =>
        new()
        {
            Repo = repo,
            InitiativeName = initiativeName,
            Tags = tags,
            Kind = Kind,
            Status = Status,
            ExcludeProposed = ExcludeProposed,
            RequiredScopeDimension = RequiredScopeDimension,
            ExcludedScopeDimensions = ExcludedScopeDimensions,
            CurrentOnly = true,
            AsOf = AsOf,
            Limit = MemorySearchDefaults.MaxLimit,
            UuidFilter = uuidFilter,
        };

    public MemoryWidenQuery Widen(IReadOnlyList<Guid> sourceUuids, int maxDepth) =>
        new()
        {
            SourceUuids = sourceUuids,
            MaxDepth = maxDepth,
            RequiredScopeDimension = RequiredScopeDimension,
            HiddenDimensions = HiddenDimensions,
            Kind = Kind,
            Status = Status,
            ExcludeProposed = ExcludeProposed,
            AsOf = AsOf,
            Limit = MemorySearchDefaults.MaxLimit,
        };
}
