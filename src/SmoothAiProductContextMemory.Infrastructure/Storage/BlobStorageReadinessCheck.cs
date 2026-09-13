using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Infrastructure.Storage;

/// <summary>
/// Object-store reachability. Reports <see cref="HealthStatus.Degraded"/> rather than unhealthy: a
/// blob hiccup must not flap readiness and pull the whole service out of rotation, because retrieval
/// of already-cached metadata still works. The probe address cannot exist, so a reachable store
/// answers "absent" without a write.
/// </summary>
public sealed class BlobStorageReadinessCheck(IBlobStorage blobStorage) : IHealthCheck
{
    internal const string UnreachableDescription = "Object store is not reachable.";

    private const string ProbeAddress = "00/00/0000000000000000000000000000000000000000000000000000000000000000";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await blobStorage.ExistsAsync(ProbeAddress, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Degraded(UnreachableDescription);
        }
    }
}
