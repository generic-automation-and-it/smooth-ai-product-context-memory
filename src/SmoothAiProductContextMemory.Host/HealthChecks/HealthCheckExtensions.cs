using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Storage;

namespace SmoothAiProductContextMemory.Host.HealthChecks;

internal static class HealthCheckExtensions
{
    internal const string ReadinessPath = "/health";
    internal const string LivenessPath = "/alive";

    private const string LivenessTag = "live";

    internal static IServiceCollection AddSmoothAiProductContextMemoryHealthChecks(this IServiceCollection services)
    {
        services.AddSingleton<MigrationReadinessState>();

        services.AddHealthChecks()
            .AddCheck<MigrationReadinessCheck>("migrations")
            .AddCheck<PostgresReadinessCheck>("postgres")
            .AddCheck<BlobStorageReadinessCheck>("blob");

        return services;
    }

    /// <summary>
    /// <see cref="ReadinessPath"/> runs every check — unhealthy answers 503, and a degraded object
    /// store still answers 200 so a blob hiccup does not pull the service out of rotation.
    /// <see cref="LivenessPath"/> carries no checks: it answers only "this process responds".
    /// </summary>
    internal static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(ReadinessPath);
        app.MapHealthChecks(
            LivenessPath,
            new HealthCheckOptions { Predicate = registration => registration.Tags.Contains(LivenessTag) });

        return app;
    }
}
