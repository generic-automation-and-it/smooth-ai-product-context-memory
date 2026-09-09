using Npgsql;
using Xunit.v3;

namespace SmoothAiProductContextMemory.TestFramework.Fixtures;

/// <summary>
/// Factory for per-test isolated databases used in L1 Infrastructure component tests.
/// Creates a fresh database on demand against the Aspire-hosted PostgreSQL container.
/// Domain-agnostic: it creates and drops databases but knows nothing about the application
/// DbContext or its migrations — the caller owns migrating the returned database.
/// </summary>
public sealed class SmoothAiProductContextMemoryTestDatabase : IAsyncDisposable
{
    private readonly string _maintenanceConnectionString;

    private SmoothAiProductContextMemoryTestDatabase(string connectionString, string databaseName, string maintenanceConnectionString)
    {
        ConnectionString = connectionString;
        DatabaseName = databaseName;
        _maintenanceConnectionString = maintenanceConnectionString;
    }

    public string ConnectionString { get; }

    public string DatabaseName { get; }

    public static async Task<SmoothAiProductContextMemoryTestDatabase> CreateAsync(
        AspireFixture aspire,
        string databaseName,
        CancellationToken cancellationToken = default,
        ITestOutputHelper? output = null)
    {
        ArgumentNullException.ThrowIfNull(aspire);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        string maintenanceConnectionString = aspire.CreateDatabaseConnectionString("postgres");
        Log(output, maintenanceConnectionString, $"Recreating database '{databaseName}'...");
        await PostgreSqlDatabaseManager.RecreateDatabaseAsync(maintenanceConnectionString, databaseName);

        string connectionString = aspire.CreateDatabaseConnectionString(databaseName);

        Log(output, connectionString, $"Database '{databaseName}' ready.");

        return new SmoothAiProductContextMemoryTestDatabase(connectionString, databaseName, maintenanceConnectionString);
    }

    /// <summary>Recreates the database. Migration is the caller's responsibility.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await PostgreSqlDatabaseManager.RecreateDatabaseAsync(_maintenanceConnectionString, DatabaseName);
    }

    public async ValueTask DisposeAsync()
    {
        // Per-test databases are never needed again; the test Postgres outlives the run, so an
        // orphaned database would accumulate on the persistent container indefinitely.
        await PostgreSqlDatabaseManager.DropDatabaseIfExistsAsync(_maintenanceConnectionString, DatabaseName);
    }

    private static void Log(ITestOutputHelper? output, string connectionString, string message)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            output?.WriteLine($"[SmoothAiProductContextMemoryTestDatabase {DateTime.UtcNow:HH:mm:ss.fff}] {b.Host}:{b.Port} — {message}");
        }
        catch (InvalidOperationException)
        {
            // Output helper is no longer active (test has ended).
        }
        catch (Exception)
        {
            try
            {
                output?.WriteLine($"[SmoothAiProductContextMemoryTestDatabase {DateTime.UtcNow:HH:mm:ss.fff}] {message}");
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
