using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
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
        MemoryGroup[] owners = await db.MemoryGroups
            .FromSqlInterpolated(
                $"""
                SELECT * FROM memory_group g
                WHERE EXISTS (
                    SELECT 1 FROM jsonb_array_elements(g.tickets) t
                    WHERE t->>'provider' = {provider} AND t->>'key' = {key}
                )
                """)
            .AsNoTracking()
            .Take(2)
            .ToArrayAsync(cancellationToken);
        if (owners.Length > 1)
        {
            throw new ConflictException("Ticket ownership is ambiguous.");
        }

        return owners.SingleOrDefault();
    }
}
