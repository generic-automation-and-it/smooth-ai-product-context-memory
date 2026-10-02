using SmoothAiProductContextMemory.Application.Features.Snapshot;
using SmoothAiProductContextMemory.Host.HealthChecks;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;

namespace SmoothAiProductContextMemory.Host.Configuration;

internal sealed class DatabaseMigrationHostedService(
    IServiceProvider services,
    MigrationReadinessState readiness,
    ILogger<DatabaseMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // An upgrade *is* a migration: say so, and how stale the last backup is, before the schema
        // changes. One structured line, emitted only when there is something to apply (HLD-006
        // LADR-04). It is visibility, never a gate — the migration runs either way.
        MigrationPosture? posture = await services.ReadMigrationPostureAsync(cancellationToken);
        if (posture is not null)
        {
            logger.LogInformation(
                "Applying {PendingMigrationCount} pending migration(s); last snapshot: {LastSnapshot}",
                posture.PendingMigrationCount,
                posture.LastSnapshot);
        }

        await services.MigrateSmoothAiProductContextMemoryAsync(cancellationToken);
        readiness.MarkCompleted();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
