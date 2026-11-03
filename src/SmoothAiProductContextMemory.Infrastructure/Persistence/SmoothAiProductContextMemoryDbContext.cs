using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Configurations;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// DbContext over the context-memory model. Deliberately exposes exactly the six entity types —
/// the model-shape guard test asserts this set, reintroducing a table for tags/facets/sources/
/// repositories/tickets (or the dropped MemoryLink) fails the build rather than passing review unnoticed.
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

    public IQueryable<LabelUsageRow> QueryLabelUsage() =>
        Database.SqlQueryRaw<LabelUsageRow>("SELECT name AS \"Name\", uses AS \"Uses\" FROM label_usage");

    /// <remarks>
    /// LATERAL unnest of the membership array, anchored on <c>g.uuid = ANY(…)</c> — the unique uuid
    /// index, so the cost is bounded by the groups asked for. The GIN index on the array serves
    /// containment queries, not this element-wise unnest, so it is deliberately not relied on here.
    /// The <c>jsonb_typeof</c> guards mirror <c>TicketLookup</c>: a membership column that is absent or
    /// not an array must not fail the read, and an element without a non-empty string provider/key is
    /// not a ticket identity — a half-ticket would render on the card as ":key".
    /// </remarks>
    public IQueryable<OwnedTicketIdentity> QueryOwnedTicketIdentities(IReadOnlyCollection<Guid> groupUuids)
    {
        if (groupUuids.Count == 0)
        {
            return Database.SqlQueryRaw<OwnedTicketIdentity>(
                """
                SELECT NULL::uuid AS "GroupUuid", NULL::text AS "Provider", NULL::text AS "Key"
                WHERE false
                """);
        }

        // Positional placeholder + unnamed parameter, as `ClassifiedMemories` does: the uuids travel as
        // a parameter, so nothing caller-supplied reaches the statement text.
        string sql = """
            SELECT g.uuid AS "GroupUuid", t->>'provider' AS "Provider", t->>'key' AS "Key"
            FROM memory_group g,
                 LATERAL jsonb_array_elements(
                     CASE WHEN jsonb_typeof(g.tickets) = 'array' THEN g.tickets ELSE '[]'::jsonb END) t
            WHERE g.uuid = ANY({0})
              AND NULLIF(t->>'provider', '') IS NOT NULL
              AND NULLIF(t->>'key', '') IS NOT NULL
            """;

        return Database.SqlQueryRaw<OwnedTicketIdentity>(sql, groupUuids.ToArray());
    }

    public void DiscardTrackedState() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SmoothAiProductContextMemoryDbContext).Assembly);
    }
}
