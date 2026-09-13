using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.DesignTime;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can build the DbContext without a running
/// host. Reads the connection string from the <c>CONNECTIONSTRINGS__SMOOTHAIPRODUCTCONTEXTMEMORY</c>
/// environment variable, falling back to a local dev default.
/// </summary>
public sealed class SmoothAiProductContextMemoryDbContextFactory : IDesignTimeDbContextFactory<SmoothAiProductContextMemoryDbContext>
{
    private const string ConnectionStringEnvVar = "CONNECTIONSTRINGS__SMOOTHAIPRODUCTCONTEXTMEMORY";
    private const string FallbackConnectionString =
        "Host=localhost;Port=15432;Database=smoothaiproductcontextmemory;Username=postgres;Password=LocalMachineAccessNoInterestingDataTestDev#Passw0rd!FirewallNotExposed";

    private static NpgsqlDataSource? _dataSource;

    public SmoothAiProductContextMemoryDbContext CreateDbContext(string[] args)
    {
        _dataSource ??= NpgsqlDataSourceFactory.Create(
            Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? FallbackConnectionString);
        var options = new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
            .UseNpgsql(_dataSource)
            .Options;

        return new SmoothAiProductContextMemoryDbContext(options);
    }
}
