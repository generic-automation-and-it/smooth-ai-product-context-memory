using NodaTime;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Common.Models;

/// <summary>
/// One ticket the memory is bound to, and how it is bound.
///
/// Identity only (<c>provider</c>, <c>key</c>), matching the graph's Ticket vertex (HLD-003
/// LADR-08): nothing descriptive is copied out of the group's stored ticket document. <c>Match</c>
/// uses the same vocabulary as <see cref="CheapMemory.TicketMatch"/>.
/// </summary>
public sealed record TicketRef(string Provider, string Key, string Match);

public static class TicketMatchValue
{
    /// <summary>The memory's group owns the ticket — the group's membership holds it.</summary>
    public const string Owner = "owner";

    /// <summary>The memory declares an <c>ABOUT</c> reference to a ticket its group does not own (HLD-003 LADR-09).</summary>
    public const string Reference = "reference";
}

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
    Instant CreatedOn,
    bool? HasBody = null)
{
    /// <summary>
    /// Set only when the query carries <c>productName</c>: <c>"initiative"</c> when the memory's group
    /// initiative is linked to the product, <c>"repo"</c> when only its repo is. Null otherwise.
    /// </summary>
    public string? ProductMatch { get; init; }

    /// <summary>True when the group's initiative is linked to more than one product. Only set with <c>productName</c>.</summary>
    public bool SharedInitiative { get; init; }

    /// <summary>
    /// <c>"owner"</c> when the memory is in the ticket's owner group, <c>"reference"</c> when it reached
    /// the query via an <c>ABOUT</c> reference, null when the query carried no ticket. It describes how
    /// <em>this</em> query reached the row; <see cref="Tickets"/> describes what the row is bound to.
    /// </summary>
    public string? TicketMatch { get; init; }

    /// <summary>
    /// The memory's ticket binding, assembled from the two places the store keeps it: the tickets its
    /// group owns (the group's membership array) and the tickets it references but does not own
    /// (<c>ABOUT</c> edges). A group accumulates tickets over time, so this is the whole set, each entry
    /// marked <see cref="TicketMatchValue.Owner"/> or <see cref="TicketMatchValue.Reference"/>.
    /// </summary>
    public IReadOnlyList<TicketRef> Tickets { get; init; } = [];

    /// <summary>
    /// The group's repository and initiative. Group-scoped, so this is the container's identity, not a
    /// scope leak — the row already carries the group uuid.
    /// </summary>
    public string? Repository { get; init; }

    public string? InitiativeName { get; init; }
}
