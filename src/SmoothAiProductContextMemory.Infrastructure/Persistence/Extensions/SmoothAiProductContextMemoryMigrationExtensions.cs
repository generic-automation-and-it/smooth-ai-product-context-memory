using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Application.Features.Snapshot;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;

public static class SmoothAiProductContextMemoryMigrationExtensions
{
    /// <summary>
    /// The startup migration posture: how many migrations this image has still to apply, and how old
    /// the last snapshot is. <c>null</c> when there is nothing to apply, so the caller logs no line
    /// (HLD-006 LADR-04: visibility, never a scheduled or blocking step). Read-only — it applies
    /// nothing; the migration itself is a separate call, and the caller orders them.
    /// </summary>
    public static async Task<MigrationPosture?> ReadMigrationPostureAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        await using var db = scope.ServiceProvider.GetRequiredService<SmoothAiProductContextMemoryDbContext>();
        int pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        if (pending == 0)
        {
            return null;
        }

        ISnapshotMetadataStore metadataStore =
            scope.ServiceProvider.GetRequiredService<ISnapshotMetadataStore>();
        SnapshotMetadata? metadata = await metadataStore.ReadAsync(cancellationToken);
        return new MigrationPosture(
            pending,
            MigrationPosture.LastSnapshotText(metadata, DateTimeOffset.UtcNow));
    }

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
        // Clear the pool without opening: opening then clearing can return the unprepared
        // connection after ClearPool and leave it idle.
        //
        // The data source must be the same singleton EF was configured with — clearing a pool
        // keyed by a different connection string is a silent no-op that leaves unprepared
        // connections live, so an unregistered data source fails loudly here.
        NpgsqlDataSource dataSource = scope.ServiceProvider.GetService<NpgsqlDataSource>()
            ?? throw new InvalidOperationException(
                "NpgsqlDataSource is not registered. Migration must clear the pool EF actually uses; "
                + "register the shared NpgsqlDataSource singleton (see AddPersistence / test migrate DI).");
        await using (NpgsqlConnection marker = dataSource.CreateConnection())
        {
            NpgsqlConnection.ClearPool(marker);
        }

        // Runtime version-pairing assert (HLD 003 / NFR-04): a wrong image otherwise surfaces later
        // as intermittent graph failures. Opened after ClearPool, so this is a fresh, prepared
        // physical connection.
        await using (NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT current_setting('server_version_num')::int / 10000,
                       (SELECT extversion FROM pg_extension WHERE extname = 'age')
                """,
                connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            int postgresMajor = reader.GetInt32(0);
            string? ageVersion = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (postgresMajor != AgeSession.PostgresMajor || ageVersion != AgeSession.ExtensionVersion)
            {
                throw new InvalidOperationException(
                    $"Postgres/AGE version pairing mismatch: expected PG {AgeSession.PostgresMajor} + AGE "
                    + $"{AgeSession.ExtensionVersion}, found PG {postgresMajor} + AGE {ageVersion ?? "<absent>"}. "
                    + "See docs/hlds/003-graph-edges-on-age/nfrs/NFR-04-version-pairing.md.");
            }
        }
    }
}
