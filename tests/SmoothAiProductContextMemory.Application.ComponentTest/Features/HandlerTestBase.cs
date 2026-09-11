using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;
using SmoothAiProductContextMemory.Infrastructure.Storage;
using SmoothAiProductContextMemory.TestFramework.Fixtures;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

[Collection("Aspire")]
public abstract class HandlerTestBase(AspireFixture aspire) : IAsyncLifetime
{
    private SmoothAiProductContextMemoryTestDatabase? _database;

    protected SmoothAiProductContextMemoryDbContext Db { get; private set; } = default!;

    protected IApplicationDbContext AppDb => Db;

    protected IBlobStorage Blob { get; private set; } = default!;

    /// <summary>Real Npgsql translation — the mapper is the thing under test in constraint cases.</summary>
    protected IDbErrorMapper ErrorMapper { get; } = new NpgsqlDbErrorMapper();

    /// <summary>Real provider search so predicates are proven against PostgreSQL, not LINQ-to-objects.</summary>
    protected IMemorySearch Search { get; private set; } = default!;

    protected ILoggerFactory Loggers { get; private set; } = default!;

    protected CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await SmoothAiProductContextMemoryTestDatabase.CreateAsync(
            aspire,
            $"app-component-{Guid.NewGuid():N}",
            Ct);

        await ApplyMigrationsAsync(_database.ConnectionString, Ct);

        var options = new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
            .UseNpgsql(_database.ConnectionString)
            .Options;

        Db = new SmoothAiProductContextMemoryDbContext(options);
        Search = new NpgsqlMemorySearch(Db);
        Loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug));

        var blobOptions = Microsoft.Extensions.Options.Options.Create(new BlobStorageOptions
        {
            Endpoint = aspire.BlobEndpoint,
            AccessKey = AspireFixture.BlobAccessKey,
            SecretKey = AspireFixture.BlobSecretKey,
            Bucket = $"app-comp-{Guid.NewGuid():N}",
        });
        Blob = new S3BlobStorage(blobOptions, Loggers.CreateLogger<S3BlobStorage>());
    }

    public async ValueTask DisposeAsync()
    {
        if (Db is not null)
        {
            await Db.DisposeAsync();
        }

        Loggers?.Dispose();

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private static async Task ApplyMigrationsAsync(string connectionString, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddDbContext<SmoothAiProductContextMemoryDbContext>(options =>
            options.UseNpgsql(connectionString));

        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.MigrateSmoothAiProductContextMemoryAsync(cancellationToken);
    }
}
