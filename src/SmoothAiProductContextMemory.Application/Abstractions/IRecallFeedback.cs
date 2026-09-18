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
/// One retrieval outcome. A single retrieval returning N memories writes N records sharing
/// <see cref="RetrievalId"/>; a retrieval that returned nothing writes one record with a null
/// <see cref="MemoryUuid"/>.
/// </summary>
public sealed record RecallFeedbackRecord(
    Guid RetrievalId,
    Guid? MemoryUuid,
    string Shape,
    DateTimeOffset OccurredOn);

/// <summary>
/// Fire-and-forget write of one retrieval's outcome. Failure must never propagate — losing a tuning
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
