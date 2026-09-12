using SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;

namespace SmoothAiProductContextMemory.Host.Configuration;

internal sealed class DatabaseMigrationHostedService(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        services.MigrateSmoothAiProductContextMemoryAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
