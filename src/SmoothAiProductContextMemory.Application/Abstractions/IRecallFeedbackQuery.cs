namespace SmoothAiProductContextMemory.Application.Abstractions;

/// <summary>As-of point and record cap for the never-recalled list.</summary>
public sealed record NeverRecalledRequest(DateTimeOffset AsOf, int? Limit = 500);

/// <summary>A memory that has never been recalled, with its capture age.</summary>
public sealed record NeverRecalledRow(Guid MemoryUuid, DateTimeOffset CapturedOn);

/// <summary>Miss-rate window.</summary>
public sealed record MissRateRequest(DateTimeOffset From, DateTimeOffset To);

public sealed record MissRateResult(int Retrievals, int Misses, double MissRate);

/// <summary>
/// Read/reset surface for the three tuning questions (HLD-004 NFR-03). Consumed occasionally by a
/// human practitioner while tuning. Never exposes content or query text — identity, count and time only.
/// </summary>
public interface IRecallFeedbackQuery
{
    /// <summary>
    /// Memories that have never been recalled, excluding those captured within the recency grace so a
    /// new memory does not pollute the signal.
    /// </summary>
    Task<IReadOnlyList<NeverRecalledRow>> NeverRecalledAsync(
        NeverRecalledRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// How often retrieval returned nothing over a window — the denominator is distinct retrievals,
    /// the numerator those with no memory returned.
    /// </summary>
    Task<MissRateResult> MissRateAsync(MissRateRequest request, CancellationToken cancellationToken);

    /// <summary>Resettable baseline (LADR-04). Legitimate, not destructive — feedback is disposable.</summary>
    Task<int> ResetAsync(CancellationToken cancellationToken);
}
