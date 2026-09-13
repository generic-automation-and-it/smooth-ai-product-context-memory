using SmoothAiProductContextMemory.Host.HealthChecks;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;

namespace SmoothAiProductContextMemory.Host.Configuration;

internal sealed class DatabaseMigrationHostedService(
    IServiceProvider services,
    MigrationReadinessState readiness) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await services.MigrateSmoothAiProductContextMemoryAsync(cancellationToken);
        readiness.MarkCompleted();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
