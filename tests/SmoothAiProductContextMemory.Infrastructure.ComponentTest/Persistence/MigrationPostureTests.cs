using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Application.Features.Snapshot;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;
using SmoothAiProductContextMemory.TestFramework.Fixtures;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// L1 — the startup migration posture over a real, <em>unmigrated</em> database, the only state in
/// which migrations are pending. HLD-006 LADR-04: an upgrade is visible as a migration, and the last
/// snapshot's age is stated with it; nothing is scheduled and nothing blocks. The three cases are the
/// decision table — pending with no snapshot, pending with a snapshot, and nothing pending (no line).
/// </summary>
[Collection("Aspire")]
public sealed class MigrationPostureTests(AspireFixture aspire) : IAsyncLifetime
{
    private readonly FakeSnapshotMetadataStore _metadata = new();
    private SmoothAiProductContextMemoryTestDatabase? _database;
    private NpgsqlDataSource? _dataSource;
    private ServiceProvider? _provider;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        aspire.SetOutput(TestContext.Current?.TestOutputHelper);
        _database = await SmoothAiProductContextMemoryTestDatabase.CreateAsync(
            aspire,
            $"migration-posture-{Guid.NewGuid():N}",
            Ct);
        _dataSource = NpgsqlDataSourceFactory.Create(_database.ConnectionString);

        var services = new ServiceCollection();
        services.AddSingleton(_dataSource);
        services.AddDbContext<SmoothAiProductContextMemoryDbContext>(options =>
            options.UseNpgsql(_dataSource, npgsql => npgsql.UseSmoothAiProductContextMemoryHistory()));
        services.AddSingleton<ISnapshotMetadataStore>(_metadata);
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Pending_migrations_with_no_snapshot_state_the_absence()
    {
        MigrationPosture? posture = await _provider!.ReadMigrationPostureAsync(Ct);

        posture.ShouldNotBeNull();
        posture!.PendingMigrationCount.ShouldBeGreaterThan(0);
        posture.LastSnapshot.ShouldBe(MigrationPosture.NoSnapshotRecorded);
    }

    [Fact]
    public async Task Pending_migrations_with_a_recorded_snapshot_state_its_age()
    {
        _metadata.Metadata = new SnapshotMetadata(
            DateTimeOffset.UtcNow.AddDays(-3),
            new SnapshotCounts(0, 0, 0, 0, 0, 0, 0),
            DanglingReferences: 0,
            UnreferencedObjects: 0);

        MigrationPosture? posture = await _provider!.ReadMigrationPostureAsync(Ct);

        posture.ShouldNotBeNull();
        posture!.PendingMigrationCount.ShouldBeGreaterThan(0);
        posture.LastSnapshot.ShouldContain("days ago");
    }

    [Fact]
    public async Task No_pending_migrations_reports_no_line()
    {
        // The only way to have nothing pending is to have applied them — which is what makes this the
        // "no line on an ordinary start" case, not a mock.
        await _provider!.MigrateSmoothAiProductContextMemoryAsync(Ct);

        (await _provider!.ReadMigrationPostureAsync(Ct)).ShouldBeNull();
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
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

    private sealed class FakeSnapshotMetadataStore : ISnapshotMetadataStore
    {
        public SnapshotMetadata? Metadata { get; set; }

        public Task<SnapshotMetadata?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Metadata);

        public Task WriteAsync(SnapshotMetadata metadata, CancellationToken cancellationToken = default)
        {
            Metadata = metadata;
            return Task.CompletedTask;
        }
    }
}
