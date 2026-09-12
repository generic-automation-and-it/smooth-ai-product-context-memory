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

    DbSet<MemoryLink> MemoryLinks { get; }

    DatabaseFacade Database { get; }

    IQueryable<LabelUsageRow> QueryLabelUsage();

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
