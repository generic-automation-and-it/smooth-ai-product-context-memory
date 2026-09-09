using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;

public static class SmoothAiProductContextMemoryMigrationExtensions
{
    /// <summary>Applies pending migrations to the database behind this provider.</summary>
    public static async Task MigrateSmoothAiProductContextMemoryAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        await using var db = scope.ServiceProvider.GetRequiredService<SmoothAiProductContextMemoryDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
