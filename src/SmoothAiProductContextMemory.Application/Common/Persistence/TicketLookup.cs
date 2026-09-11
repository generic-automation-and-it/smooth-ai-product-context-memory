using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Common.Persistence;

public static class TicketLookup
{
    public static async Task<MemoryGroup?> FindGroupByTicketAsync(
        IApplicationDbContext db,
        string provider,
        string key,
        CancellationToken cancellationToken)
    {
        return await db.MemoryGroups
            .FromSqlInterpolated(
                $"""
                SELECT * FROM memory_group g
                WHERE EXISTS (
                    SELECT 1 FROM jsonb_array_elements(g.tickets) t
                    WHERE t->>'provider' = {provider} AND t->>'key' = {key}
                )
                """)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
    }
}
