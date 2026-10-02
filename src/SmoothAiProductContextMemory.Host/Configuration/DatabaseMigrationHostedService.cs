using SmoothAiProductContextMemory.Application.Features.Snapshot;
using SmoothAiProductContextMemory.Host.HealthChecks;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;

namespace SmoothAiProductContextMemory.Host.Configuration;

internal class DatabaseMigrationHostedService(
    IServiceProvider services,
    MigrationReadinessState readiness,
    ILogger<DatabaseMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        MigrationPosture? posture;
        try
        {
            posture = await ReadPostureAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Describing the migration must never block the migration it describes (visibility, never
            // a gate). The exception *type* only — a connection string can appear in a message, and
            // this line is a log.
            logger.LogWarning(
                "Migration posture unavailable ({ExceptionType}); applying migrations anyway.",
                exception.GetType().Name);
            posture = null;
        }

        // One structured line, emitted only when there is something to apply, so an ordinary start is
        // silent. An upgrade *is* a migration; the snapshot age is the cue to take a fresh one.
        if (posture is not null)
        {
            logger.LogInformation(
                "Applying {PendingMigrationCount} pending migration(s); last snapshot: {LastSnapshot}",
                posture.PendingMigrationCount,
                posture.LastSnapshot);
        }

        await MigrateAsync(cancellationToken);
        readiness.MarkCompleted();
    }

    // Seams for the L0 test: the real reads touch PostgreSQL and the metadata file, so the test
    // overrides them rather than standing up a database. `StartAsync` is what must be pinned — the
    // emission *and* the decision to stay silent both live here, not in the posture reader.
    internal virtual Task<MigrationPosture?> ReadPostureAsync(CancellationToken cancellationToken) =>
        services.ReadMigrationPostureAsync(cancellationToken);

    internal virtual Task MigrateAsync(CancellationToken cancellationToken) =>
        services.MigrateSmoothAiProductContextMemoryAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
