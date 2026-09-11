using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

internal static class TestEntities
{
    public const long DefaultInitiativeId = 1;

    public static MemoryGroup NewGroup(
        string scopeDimension = MemoryGroup.ScopeDimensionValue.Product,
        string? repo = null,
        List<TicketDocument>? tickets = null) => new()
    {
        Uuid = Guid.NewGuid(),
        ScopeDimension = scopeDimension,
        InitiativeId = DefaultInitiativeId,
        Repo = repo,
        Tickets = tickets ?? [],
        CreatedOn = DateTimeOffset.UtcNow,
    };

    public static Memory NewMemory(long groupId, string name, string description)
    {
        Guid uuid = Guid.NewGuid();
        return new Memory
        {
            Uuid = uuid,
            LineageId = uuid,
            GroupId = groupId,
            Name = name,
            Description = description,
            SubjectSlug = Slug.Subject(description),
            Tags = [],
            Facets = [],
        };
    }
}
