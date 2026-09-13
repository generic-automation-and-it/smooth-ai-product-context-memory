using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Relational-store reachability, probed through the same shared <see cref="NpgsqlDataSource"/> the
/// application actually uses — a probe against a second data source would pass while the pool the
/// request path borrows from is dead. The failure description is a fixed string: a PostgreSQL error
/// message can carry the offending value in its <c>DETAIL:</c> clause (NFR-05).
/// </summary>
public sealed class PostgresReadinessCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    internal const string UnreachableDescription = "PostgreSQL is not reachable.";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException)
        {
            return HealthCheckResult.Unhealthy(UnreachableDescription);
        }
        catch (TimeoutException)
        {
            return HealthCheckResult.Unhealthy(UnreachableDescription);
        }
    }
}
