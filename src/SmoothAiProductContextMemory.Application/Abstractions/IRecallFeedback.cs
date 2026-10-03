namespace SmoothAiProductContextMemory.Application.Abstractions;

/// <summary>
/// Bounded retrieval-shape categories (HLD-004 LADR-03, closed here). A shape is the kind of
/// retrieval the caller made, not its free text — content must never enter a feedback record, so the
/// schema constrains this set.
/// </summary>
public static class RetrievalShape
{
    public const string FreeText = "free_text";
    public const string FacetOnly = "facet_only";
    public const string TicketScoped = "ticket_scoped";
    public const string GroupScoped = "group_scoped";
    public const string Unfiltered = "unfiltered";

    public static readonly string[] All =
        [FreeText, FacetOnly, TicketScoped, GroupScoped, Unfiltered];

    public static bool IsKnown(string shape) =>
        All.Contains(shape, StringComparer.Ordinal);
}

/// <summary>
/// Content-free bounded attribution. AllPurposes is a tuning filter only, never a stored purpose.
/// </summary>
public static class RecallPurpose
{
    public const string DirectRetrieval = "direct_retrieval";
    public const string ServiceRetrieval = "service_retrieval";
    public const string CaptureComparison = "capture_comparison";
    public const string AllPurposes = "all";
    public static readonly string[] All = [DirectRetrieval, ServiceRetrieval, CaptureComparison];
    public static bool IsKnown(string purpose) => All.Contains(purpose, StringComparer.Ordinal);
    public static bool IsFilter(string purpose) => purpose == AllPurposes || IsKnown(purpose);
}

/// <summary>
/// One core pass outcome: N hits share RetrievalId, a miss has a null MemoryUuid.
/// CallerRequestId groups observed internal passes without storing content or query text.
/// </summary>
public sealed record RecallFeedbackRecord(
    Guid RetrievalId,
    Guid? MemoryUuid,
    string Shape,
    DateTimeOffset OccurredOn,
    string Purpose = RecallPurpose.DirectRetrieval,
    Guid? CallerRequestId = null);

/// <summary>
/// Guarded synchronous write of one retrieval's outcome. Failure must never propagate — losing a tuning
/// signal is acceptable, losing a recall is not.
/// </summary>
public interface IRecallFeedback
{
    /// <summary>
    /// Writes the records best-effort. Never throws. The retrieval result set is already finalised
    /// when this is called, and this method never reads the feedback set, so it cannot influence
    /// ranking or retrieval.
    /// </summary>
    void Record(RecallFeedbackRecord[] records);
}
