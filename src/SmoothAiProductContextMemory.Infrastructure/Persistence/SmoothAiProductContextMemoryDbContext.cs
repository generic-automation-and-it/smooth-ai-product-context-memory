using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Configurations;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// DbContext over the context-memory model. Deliberately exposes exactly the seven entity types —
/// the model-shape guard test asserts this set, reintroducing a table for tags/facets/sources/
/// repositories/tickets fails the build rather than passing review unnoticed.
/// </summary>
public sealed class SmoothAiProductContextMemoryDbContext(DbContextOptions<SmoothAiProductContextMemoryDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    public DbSet<Initiative> Initiatives => Set<Initiative>();

    public DbSet<Label> Labels => Set<Label>();

    public DbSet<MemoryGroup> MemoryGroups => Set<MemoryGroup>();

    public DbSet<GroupDescription> GroupDescriptions => Set<GroupDescription>();

    public DbSet<Memory> Memories => Set<Memory>();

    public DbSet<MemoryVersion> MemoryVersions => Set<MemoryVersion>();

    public DbSet<MemoryLink> MemoryLinks => Set<MemoryLink>();

    public IQueryable<LabelUsageRow> QueryLabelUsage() =>
        Database.SqlQueryRaw<LabelUsageRow>("SELECT name AS \"Name\", uses AS \"Uses\" FROM label_usage");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SmoothAiProductContextMemoryDbContext).Assembly);
    }
}
