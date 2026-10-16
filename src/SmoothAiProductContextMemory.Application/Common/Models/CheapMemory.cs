using NodaTime;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Common.Models;

/// <summary>One ticket reference on the memory's group. A group accumulates tickets over time.</summary>
public sealed record TicketRef(string Provider, string Key);

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

    /// <summary>
    /// The group's identity, projected with the row. These are the group's own bindings — the same
    /// fields the query filters on — not the memory's scope, so they are not a scope leak: the group
    /// is the memory's container and the row already carries its uuid.
    /// </summary>
    public IReadOnlyList<TicketRef> Tickets { get; init; } = [];

    public string? Repository { get; init; }

    public string? InitiativeName { get; init; }
}
