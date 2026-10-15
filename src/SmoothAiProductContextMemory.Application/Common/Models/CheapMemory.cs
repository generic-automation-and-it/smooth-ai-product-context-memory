using NodaTime;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Common.Models;

public sealed record CheapMemory(
    Guid Uuid,
    Guid GroupUuid,
    string Name,
    string Description,
    string Statement,
    string ContentSummary,
    string Kind,
    IReadOnlyList<string> Facets,
    IReadOnlyList<string> Tags,
    string Status,
    short Confidence,
    string ScopeDimension,
    string? ScopeIdentifier,
    Instant ValidFrom,
    Instant? ValidUntil,
    int Version,
    bool IsCurrent,
    IReadOnlyList<SourceDocument> Sources,
    Instant CreatedOn)
{
    /// <summary>
    /// Set only when the query carries <c>productName</c>: <c>"initiative"</c> when the memory's group
    /// initiative is linked to the product, <c>"repo"</c> when only its repo is. Null otherwise.
    /// </summary>
    public string? ProductMatch { get; init; }

    /// <summary>True when the group's initiative is linked to more than one product. Only set with <c>productName</c>.</summary>
    public bool SharedInitiative { get; init; }
}
