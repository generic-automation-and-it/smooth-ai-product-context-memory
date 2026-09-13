using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmoothAiProductContextMemory.Host.HealthChecks;

internal sealed class MigrationReadinessCheck(MigrationReadinessState state) : IHealthCheck
{
    internal const string PendingDescription = "Database migrations have not completed.";

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(state.Completed
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy(PendingDescription));
}
