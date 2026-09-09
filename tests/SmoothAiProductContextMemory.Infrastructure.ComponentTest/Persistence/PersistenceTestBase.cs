using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    protected SmoothAiProductContextMemoryDbContext Db { get; private set; } = default!;

    protected CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await SmoothAiProductContextMemoryTestDatabase.CreateAsync(
            aspire,
            $"infra-component-{Guid.NewGuid():N}",
            Ct);

        await ApplyMigrationsAsync(_database.ConnectionString, Ct);

        var options = new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
            .UseNpgsql(_database.ConnectionString)
            .Options;

        Db = new SmoothAiProductContextMemoryDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        if (Db is not null)
        {
            await Db.DisposeAsync();
        }

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
