using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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

        // Physical-connection initialisers run once. Connections opened before CREATE EXTENSION
        // skipped LOAD; leaving them in the pool would fail graph statements until process restart.
        NpgsqlDataSource? dataSource = scope.ServiceProvider.GetService<NpgsqlDataSource>();
        if (dataSource is not null)
        {
            await using NpgsqlConnection pooled = await dataSource.OpenConnectionAsync(cancellationToken);
            NpgsqlConnection.ClearPool(pooled);
        }
        else
        {
            string? connectionString = db.Database.GetConnectionString();
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                await using var pooled = new NpgsqlConnection(connectionString);
                NpgsqlConnection.ClearPool(pooled);
            }
        }
    }
}
