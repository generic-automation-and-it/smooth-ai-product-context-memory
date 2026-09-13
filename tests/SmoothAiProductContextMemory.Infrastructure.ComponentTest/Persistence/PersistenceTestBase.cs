using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;
using SmoothAiProductContextMemory.TestFramework.Fixtures;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// L1 base — provisions a fresh, migrated, isolated PostgreSQL database per test against the
/// shared <see cref="AspireFixture"/>. Each test is isolated by database, not by Respawn, so
/// constraint tests never see a neighbouring test's rows. The fixture is domain-agnostic and only
/// creates the database; migrations are run here because the application DbContext is an
/// Infrastructure concern, not a TestFramework one.
/// </summary>
[Collection("Aspire")]
public abstract class PersistenceTestBase(AspireFixture aspire) : IAsyncLifetime
{
    private SmoothAiProductContextMemoryTestDatabase? _database;
    private NpgsqlDataSource? _dataSource;

    protected SmoothAiProductContextMemoryDbContext Db { get; private set; } = default!;

    protected NpgsqlDataSource DataSource => _dataSource!;

    protected string ConnectionString => _database!.ConnectionString;

    protected CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await SmoothAiProductContextMemoryTestDatabase.CreateAsync(
            aspire,
            $"infra-component-{Guid.NewGuid():N}",
            Ct);

        _dataSource = NpgsqlDataSourceFactory.Create(_database.ConnectionString);
        await ApplyMigrationsAsync(_dataSource, Ct);

        var options = new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
            .UseNpgsql(_dataSource)
            .Options;

        Db = new SmoothAiProductContextMemoryDbContext(options);
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

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private static async Task ApplyMigrationsAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddDbContext<SmoothAiProductContextMemoryDbContext>(options =>
            options.UseNpgsql(dataSource));

        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.MigrateSmoothAiProductContextMemoryAsync(cancellationToken);
    }
}
