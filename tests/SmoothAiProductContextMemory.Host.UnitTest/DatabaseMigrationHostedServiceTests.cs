using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Features.Snapshot;
using SmoothAiProductContextMemory.Host.Configuration;
using SmoothAiProductContextMemory.Host.HealthChecks;
using SmoothAiProductContextMemory.TestFramework.Logging;

namespace SmoothAiProductContextMemory.Host.UnitTest;

/// <summary>
/// L0 — the startup line is the whole deliverable: an upgrade must be <em>visible</em>. The posture
/// reader is covered at L1 against a real database; <c>StartAsync</c> is pinned here, because the
/// emission <em>and</em> the decision to stay silent both live in it. Deleting the log call, or
/// logging unconditionally, each fails a test below.
/// </summary>
public sealed class DatabaseMigrationHostedServiceTests : IDisposable
{
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _factory;

    public DatabaseMigrationHostedServiceTests()
    {
        _factory = LoggerFactory.Create(builder => builder.AddProvider(_logs));
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Pending_migrations_log_one_line_with_the_count_and_the_age()
    {
        TestableService service = Service(new MigrationPosture(3, "2 days ago"));

        await service.StartAsync(TestContext.Current.CancellationToken);

        _logs.Records.ShouldContain(record =>
            record.Contains("Applying 3 pending migration(s); last snapshot: 2 days ago"));
        service.Migrated.ShouldBeTrue("the line is visibility, never a gate");
    }

    [Fact]
    public async Task No_pending_migrations_log_no_line()
    {
        TestableService service = Service(posture: null);

        await service.StartAsync(TestContext.Current.CancellationToken);

        _logs.Records.ShouldNotContain(record => record.Contains("pending migration"));
        service.Migrated.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_posture_read_warns_and_still_migrates()
    {
        TestableService service = Service(posture: null, throwOnRead: true);

        await service.StartAsync(TestContext.Current.CancellationToken);

        _logs.Records.ShouldContain(record => record.Contains("Migration posture unavailable"));
        service.Migrated.ShouldBeTrue("a metadata-store failure must not fail the boot");
    }

    private TestableService Service(MigrationPosture? posture, bool throwOnRead = false) =>
        new(posture, throwOnRead, _factory.CreateLogger<DatabaseMigrationHostedService>());

    private sealed class TestableService(
        MigrationPosture? posture,
        bool throwOnRead,
        ILogger<DatabaseMigrationHostedService> logger)
        : DatabaseMigrationHostedService(
            new ServiceCollection().BuildServiceProvider(), new MigrationReadinessState(), logger)
    {
        public bool Migrated { get; private set; }

        internal override Task<MigrationPosture?> ReadPostureAsync(CancellationToken cancellationToken) =>
            throwOnRead
                ? Task.FromException<MigrationPosture?>(new UnauthorizedAccessException("denied"))
                : Task.FromResult(posture);

        internal override Task MigrateAsync(CancellationToken cancellationToken)
        {
            Migrated = true;
            return Task.CompletedTask;
        }
    }
}
