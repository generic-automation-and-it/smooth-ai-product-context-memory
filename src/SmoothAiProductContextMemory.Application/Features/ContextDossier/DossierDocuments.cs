namespace SmoothAiProductContextMemory.Application.Features.ContextDossier;

/// <summary>
/// Stated, configurable item limit (HLD-005 NFR-03). Provisional until the reference workflows supply
/// validated numbers; recorded per export, never cited as a specification.
/// </summary>
public static class DossierDefaults
{
    public const int ItemLimit = 500;
}

/// <summary>
/// Bounded omission-reason set (HLD-005 NFR-04). Free-text reasons are forbidden: a bounded set is
/// countable, and free text is how unclassifiable omissions accumulate unnoticed. Adding a value
/// later changes the meaning of every earlier bundle.
/// </summary>
public static class DossierOmissionReason
{
    public const string CapReached = "cap reached";

    public const string DepthReached = "depth reached";

    public const string UnreadableBody = "unreadable body";

    public const string CollapsedIntoAnotherClaim = "collapsed into another claim";

    public const string HiddenByScope = "hidden by scope";

    public static readonly string[] All =
        [CapReached, DepthReached, UnreadableBody, CollapsedIntoAnotherClaim, HiddenByScope];

    public static bool IsValid(string reason) => All.Contains(reason, StringComparer.Ordinal);
}

/// <summary>The stated combination rule (HLD-005 LADR-03 / BR-18).</summary>
public static class DossierCombinationRule
{
    public const string Value = "Within a category values are alternatives (any); across categories they are conjunctive (all).";
}

/// <summary>
/// The recorded effective selection — sufficient to repeat the export (BR-20, LADR-14). Stored times are
/// data; this carries no generation timestamp (NFR-02).
/// </summary>
public sealed record DossierSelectionPlan(
    string? Repo,
    string? InitiativeName,
    string? TicketProvider,
    string? TicketKey,
    IReadOnlyList<string> Tags,
    string? Kind,
    string? Status,
    string? ScopeDimension,
    bool IncludeHistory,
    DateTimeOffset? AsOf,
    int WidenDepth,
    string CombinationRule,
    string HistoryPolicy,
    string RetrievalPolicy);

/// <summary>
/// What the selection reached and what it stopped short of. No hidden count is disclosed (NFR-01);
/// <see cref="HiddenPathDropped"/> is a generic disclosure, not an itemized list.
/// </summary>
public sealed record DossierReach(
    int WidenDepth,
    int Anchors,
    int Widened,
    int Selected,
    int Edges,
    bool HiddenPathDropped);

/// <summary>One limit actually reached, named (NFR-03).</summary>
public sealed record DossierLimitHit(string Limit, int Value);

/// <summary>
/// The bundle's manifest: the effective selection, what was reached, every limit actually reached, and
/// the selected count that the reconciliation starts from (NFR-04). <see cref="SelectedCount"/> must
/// equal the number of bundle items — present plus omitted-with-reason.
/// </summary>
public sealed record DossierManifest(
    DossierSelectionPlan Selection,
    int SelectedCount,
    DossierReach Reach,
    IReadOnlyList<DossierLimitHit> LimitsHit,
    bool NoMatch);

/// <summary>An omission, with a reason from the bounded set (NFR-04).</summary>
public sealed record DossierOmittedItem(Guid Uuid, string Reason);

/// <summary>One source document citation (NFR-05).</summary>
public sealed record DossierSource(string Kind, string Reference, DateTimeOffset? CapturedAt);

public static class DossierBodyState
{
    public const string None = "none";

    public const string Inlined = "inlined";

    public const string Missing = "missing";

    public const string NonText = "non-text";

    public static readonly string[] All = [None, Inlined, Missing, NonText];
}

/// <summary>
/// One selected claim: a single version of a memory, with its hydrated body. <see cref="ReachedVia"/>
/// carries the reason each path added it (BR-19), not just that it was reached.
/// </summary>
public sealed record DossierMemory(
    Guid Uuid,
    Guid GroupUuid,
    string Name,
    string Description,
    string Statement,
    string ContentSummary,
    string Kind,
    string Status,
    short Confidence,
    string ScopeDimension,
    string? ScopeIdentifier,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil,
    int Version,
    bool IsCurrent,
    DateTimeOffset CreatedOn,
    IReadOnlyList<DossierSource> Sources,
    string? BodyText,
    string BodyState,
    IReadOnlyList<string> ReachedVia);

/// <summary>An edge between two selected memories, with the reason it was recorded (BR-11).</summary>
public sealed record DossierEdge(Guid SourceUuid, Guid TargetUuid, string Relation, string Reason);

/// <summary>The deterministic bundle: selected claims, the edges between them, and the manifest.</summary>
public sealed record DossierBundle(
    IReadOnlyList<DossierMemory> Items,
    IReadOnlyList<DossierEdge> Edges,
    IReadOnlyList<DossierOmittedItem> Omitted,
    DossierManifest Manifest);

/// <summary>Preview volume: selected count and the reach that produced it, without any body.</summary>
public sealed record DossierVolume(int Selected, int Anchors, int Widened, int Edges);

/// <summary>
/// Estimated composition cost, with its assumptions and uncertainty, and monetary availability
/// (HLD-005 NFR-03). Monetary cost is deliberately <see cref="Unavailable"/> for now — no pricing
/// provider exists.
/// </summary>
public sealed record DossierCostEstimate(
    bool MonetaryAvailable,
    string? MonetaryCost,
    string Assumptions,
    string Uncertainty);
