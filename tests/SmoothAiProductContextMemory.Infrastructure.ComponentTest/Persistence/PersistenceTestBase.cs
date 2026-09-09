using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.TestFramework.Fixtures;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// L1 base — provisions a fresh, migrated, isolated PostgreSQL database per test against the
/// shared <see cref="AspireFixture"/>. Each test is isolated by database, not by Respawn, so
/// constraint tests never see a neighbouring test's rows.
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
}
