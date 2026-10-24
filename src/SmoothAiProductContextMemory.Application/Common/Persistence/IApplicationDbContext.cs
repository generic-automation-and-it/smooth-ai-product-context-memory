using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Common.Persistence;

public interface IApplicationDbContext
{
    DbSet<Initiative> Initiatives { get; }

    DbSet<Label> Labels { get; }

    DbSet<MemoryGroup> MemoryGroups { get; }

    DbSet<GroupDescription> GroupDescriptions { get; }

    DbSet<Memory> Memories { get; }

    DbSet<MemoryVersion> MemoryVersions { get; }

    DatabaseFacade Database { get; }

    IQueryable<LabelUsageRow> QueryLabelUsage();

    /// <summary>
    /// The tickets the given groups own, as identity only. The membership array is unnested in SQL, so
    /// a read never carries the stored ticket documents; an element without a string provider or key is
    /// left out rather than surfaced as a half-ticket.
    /// </summary>
    IQueryable<OwnedTicketIdentity> QueryOwnedTicketIdentities(IReadOnlyCollection<Guid> groupUuids);

    void DiscardTrackedState();

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
