using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
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
    private NpgsqlDataSource? _dataSource;

    protected SmoothAiProductContextMemoryDbContext Db { get; private set; } = default!;

    protected IApplicationDbContext AppDb => Db;

    /// <summary>Counts every <c>SaveChanges</c> on <see cref="Db"/>, so a read path can assert it issued none.</summary>
    protected SaveChangesCounter SaveChanges { get; } = new();

    protected IBlobStorage Blob { get; private set; } = default!;

    /// <summary>Real Npgsql translation — the mapper is the thing under test in constraint cases.</summary>
    protected IDbErrorMapper ErrorMapper { get; } = new NpgsqlDbErrorMapper();

    /// <summary>Real provider search so predicates are proven against PostgreSQL, not LINQ-to-objects.</summary>
    protected IMemorySearch Search { get; private set; } = default!;

    protected IMemoryGraph Graph { get; private set; } = default!;

    protected ILoggerFactory Loggers { get; private set; } = default!;

    protected CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await SmoothAiProductContextMemoryTestDatabase.CreateAsync(
            aspire,
            $"app-component-{Guid.NewGuid():N}",
            Ct);

        _dataSource = NpgsqlDataSourceFactory.Create(_database.ConnectionString);
        await ApplyMigrationsAsync(_dataSource, Ct);

        var options = new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
            .UseNpgsql(_dataSource, npgsql => npgsql.UseSmoothAiProductContextMemoryHistory())
            .AddInterceptors(SaveChanges)
            .Options;

        Db = new SmoothAiProductContextMemoryDbContext(options);
        Search = new NpgsqlMemorySearch(Db);
        Graph = new NpgsqlMemoryGraph(Db);
        Loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug));

        var blobOptions = Microsoft.Extensions.Options.Options.Create(new BlobStorageOptions
        {
            Endpoint = aspire.BlobEndpoint,
            AccessKey = AspireFixture.BlobAccessKey,
            SecretKey = AspireFixture.BlobSecretKey,
            Bucket = $"app-comp-{Guid.NewGuid():N}",
        });
        Blob = new S3BlobStorage(blobOptions, new TestHttpClientFactory(), Loggers.CreateLogger<S3BlobStorage>());
    }

    public async ValueTask DisposeAsync()
    {
        if (Db is not null)
        {
            await Db.DisposeAsync();
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        Loggers?.Dispose();

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private static async Task ApplyMigrationsAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddDbContext<SmoothAiProductContextMemoryDbContext>(options =>
            options.UseNpgsql(dataSource, npgsql => npgsql.UseSmoothAiProductContextMemoryHistory()));

        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.MigrateSmoothAiProductContextMemoryAsync(cancellationToken);
    }

    protected sealed class SaveChangesCounter : SaveChangesInterceptor
    {
        private int _count;

        public int Count => _count;

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }
}
