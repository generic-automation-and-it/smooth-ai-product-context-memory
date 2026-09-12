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
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil,
    int Version,
    bool IsCurrent);
