using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>Factory helpers for building graph rows in L1 tests. The seeded <c>to-be-decided</c> initiative is id 1.</summary>
internal static class TestEntities
{
    public const long DefaultInitiativeId = 1;

    public static MemoryGroup NewGroup(
        string scopeDimension = MemoryGroup.ScopeDimensionValue.Product,
        string? scopeIdentifier = null,
        string? repo = null,
        string? repoUrl = null,
        long initiativeId = DefaultInitiativeId,
        List<TicketDocument>? tickets = null) => new()
    {
        Uuid = Guid.NewGuid(),
        ScopeDimension = scopeDimension,
        ScopeIdentifier = scopeIdentifier,
        InitiativeId = initiativeId,
        Repo = repo,
        RepoUrl = repoUrl,
        Tickets = tickets ?? [],
        CreatedOn = DateTimeOffset.UtcNow,
    };

    public static Memory NewMemory(long groupId, string name, string description, string? subjectSlug = null)
    {
        Guid uuid = Guid.NewGuid();
        return new Memory
        {
            Uuid = uuid,
            LineageId = uuid,
            GroupId = groupId,
            Name = name,
            Description = description,
            SubjectSlug = subjectSlug ?? Slug.Subject(description),
            Tags = [],
            Facets = [],
        };
    }

    public static MemoryVersion NewVersion(long memoryId, int version, string statement, bool isCurrent = true,
        DateTimeOffset? validFrom = null, DateTimeOffset? validUntil = null, string? kind = null,
        List<SourceDocument>? sources = null) => new()
    {
        MemoryId = memoryId,
        Version = version,
        IsCurrent = isCurrent,
        Statement = statement,
        ContentSummary = $"Summary of {statement}",
        Kind = kind ?? MemoryVersion.KindValue.Decision,
        Confidence = 80,
        Status = MemoryVersion.MemoryVersionStatus.Approved,
        Sources = sources ?? [],
        ValidFrom = validFrom ?? DateTimeOffset.UtcNow.AddDays(-30),
        ValidUntil = validUntil,
        CreatedOn = DateTimeOffset.UtcNow,
    };
}
