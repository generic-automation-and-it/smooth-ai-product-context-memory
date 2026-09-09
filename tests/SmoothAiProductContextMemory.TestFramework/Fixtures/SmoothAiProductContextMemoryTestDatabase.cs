using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;
using Xunit.v3;

namespace SmoothAiProductContextMemory.TestFramework.Fixtures;

/// <summary>
/// Factory for per-test isolated databases used in L1 Infrastructure component tests.
/// Creates a fresh database on demand against the Aspire-hosted PostgreSQL container.
/// </summary>
/// <remarks>
/// Once EF Core is wired up, extend <see cref="CreateAsync"/> to register the DbContext
/// and run migrations before returning the handle.
/// </remarks>
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

        await ApplyMigrationsAsync(connectionString, cancellationToken);

        Log(output, connectionString, $"Database '{databaseName}' ready.");

        return new SmoothAiProductContextMemoryTestDatabase(connectionString, databaseName, maintenanceConnectionString);
    }

    public async Task ResetAsync()
    {
        await PostgreSqlDatabaseManager.RecreateDatabaseAsync(_maintenanceConnectionString, DatabaseName);
        await ApplyMigrationsAsync(ConnectionString);
    }

    private static async Task ApplyMigrationsAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var services = new ServiceCollection();
        services.AddDbContext<SmoothAiProductContextMemoryDbContext>(options =>
            options.UseNpgsql(connectionString));

        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.MigrateSmoothAiProductContextMemoryAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
