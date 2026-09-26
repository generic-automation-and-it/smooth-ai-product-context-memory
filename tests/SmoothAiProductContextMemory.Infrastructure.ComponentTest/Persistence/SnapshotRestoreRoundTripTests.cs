using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Application.Features.Restore;
using SmoothAiProductContextMemory.Application.Features.Snapshot;
using SmoothAiProductContextMemory.Application.Features.Verify;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;
using SmoothAiProductContextMemory.Infrastructure.Storage;
using SmoothAiProductContextMemory.Infrastructure.Storage.Snapshot;
using SmoothAiProductContextMemory.TestFramework.Fixtures;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// L1 round-trip: seed a corpus (two memories, one with a superseded version and a blob body, one
/// LINKS edge, a ticket parent/child hierarchy), snapshot via the real repository + tar archive,
/// restore into a fresh scratch database, and assert the reconciliation closes — relational counts,
/// traversal, and that the TICKET_PARENT direction survives the capture->restore round trip.
/// </summary>
[Collection("Aspire")]
public sealed class SnapshotRestoreRoundTripTests : PersistenceTestBase
{
    private const string Provider = "jira";

    private readonly Lazy<S3BlobStorage> _blob;
    private readonly AspireFixture _aspire;

    private NpgsqlSnapshotRepository Repository => new(Blob, Blob);

    private TarSnapshotArchive Archive => new();

    private S3BlobStorage Blob => _blob.Value;

    private ILoggerFactory Loggers => LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug));

    public SnapshotRestoreRoundTripTests(AspireFixture aspire) : base(aspire)
    {
        _aspire = aspire;
        _blob = new Lazy<S3BlobStorage>(() => CreateBlob("snap-shot"));
    }

    private S3BlobStorage CreateBlob(string prefix) => new(
        Microsoft.Extensions.Options.Options.Create(new BlobStorageOptions
        {
            Endpoint = _aspire.BlobEndpoint,
            AccessKey = AspireFixture.BlobAccessKey,
            SecretKey = AspireFixture.BlobSecretKey,
            Bucket = $"{prefix}-{Guid.NewGuid():N}",
        }),
        new TestHttpClientFactory(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<S3BlobStorage>.Instance);

    [Fact]
    public async Task SnapshotThenRestore_Reconciles_And_PreservesTicketDirection()
    {
        var source = await SeedCorpusAsync();

        SnapshotStore.Response snapshot = await new SnapshotStore.Handler(
            Repository,
            Archive,
            Blob,
            new FileSnapshotMetadataStore(CreateMetadataOptions()),
            Loggers.CreateLogger<SnapshotStore.Handler>())
            .Handle(
                new SnapshotStore.Request(ConnectionString, Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.tar")),
                Ct);

        snapshot.Objects.ShouldBe(1);
        snapshot.TicketEdges.ShouldBe(1);
        snapshot.Edges.ShouldBe(1);

        // Verify is offline and clean.
        VerifyArchive.Response verified = await new VerifyArchive.Handler(
            Archive,
            Loggers.CreateLogger<VerifyArchive.Handler>())
            .Handle(new VerifyArchive.Request(snapshot.DestinationPath), Ct);
        verified.IsClean.ShouldBeTrue();

        // Restore into a fresh scratch database + a fresh, empty blob bucket, so the body check
        // proves the restore wrote it rather than finding the source's copy.
        await using SmoothAiProductContextMemoryTestDatabase target = await SmoothAiProductContextMemoryTestDatabase.CreateAsync(
            _aspire,
            $"infra-roundtrip-{Guid.NewGuid():N}",
            Ct);
        await using NpgsqlDataSource targetDataSource = NpgsqlDataSourceFactory.Create(target.ConnectionString);
        await MigrateAsync(targetDataSource, Ct);
        await using var targetDb = new SmoothAiProductContextMemoryDbContext(
            new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
                .UseNpgsql(targetDataSource, npgsql => npgsql.UseSmoothAiProductContextMemoryHistory())
                .Options);

        await using S3BlobStorage targetBlob = CreateBlob("snap-target");
        (await targetBlob.ExistsAsync(source.Address, Ct)).ShouldBeFalse();

        RestoreArchive.Response restore = await new RestoreArchive.Handler(
            new NpgsqlSnapshotRepository(targetBlob, targetBlob),
            Archive,
            targetBlob,
            Loggers.CreateLogger<RestoreArchive.Handler>())
            .Handle(new RestoreArchive.Request(snapshot.DestinationPath, target.ConnectionString), Ct);

        restore.Reconciled.ShouldBeTrue();
        restore.Restored.Committed.ShouldBeTrue();
        restore.Objects.ShouldBe(1);
        restore.Restored.Memories.ShouldBe(source.Memories);
        restore.Restored.Versions.ShouldBe(source.Versions);
        restore.Restored.Vertices.ShouldBe(source.Vertices);
        restore.Restored.Edges.ShouldBe(source.Edges);
        restore.Restored.TicketVertices.ShouldBe(source.TicketVertices);
        restore.Restored.TicketEdges.ShouldBe(source.TicketEdges);
        restore.Restored.TraversalPathCount.ShouldBe(source.Edges);

        // The blob body was restored into the (previously empty) target object store.
        (await targetBlob.GetAsync(source.Address, Ct)).ShouldNotBeNull();

        // The restored ticket hierarchy reads parent->child, not inverted.
        var ticketGraph = new NpgsqlTicketGraph(targetDb);
        TicketTraversalResult traversal = await ticketGraph.TraverseAsync(
            new TicketTraversalQuery
            {
                Anchor = new TicketIdentity(Provider, "PARENT"),
                MaxDepth = 1,
                Direction = TraversalDirection.Outbound,
                RequiredScopeDimension = null,
            },
            Ct);
        traversal.Paths.ShouldContain(p => p.Hops.Any(h => h.Child.Key == "CHILD" && h.Parent.Key == "PARENT"));
    }

    [Fact]
    public async Task Restore_MissingBlobEntry_FailsLoudly_NoPartialSuccess()
    {
        var source = await SeedCorpusAsync();
        SnapshotStore.Response snapshot = await new SnapshotStore.Handler(
            Repository,
            Archive,
            Blob,
            new FileSnapshotMetadataStore(CreateMetadataOptions()),
            Loggers.CreateLogger<SnapshotStore.Handler>())
            .Handle(new SnapshotStore.Request(ConnectionString, Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.tar")), Ct);

        string hole = Path.Combine(Path.GetTempPath(), $"hole-{Guid.NewGuid():N}.tar");
        RewriteWithoutBlob(snapshot.DestinationPath, hole);

        await using SmoothAiProductContextMemoryTestDatabase target = await SmoothAiProductContextMemoryTestDatabase.CreateAsync(
            _aspire,
            $"infra-neg-{Guid.NewGuid():N}",
            Ct);
        await using NpgsqlDataSource targetDataSource = NpgsqlDataSourceFactory.Create(target.ConnectionString);
        await MigrateAsync(targetDataSource, Ct);

        // A missing blob entry must fail the restore loudly before any DB mutation.
        await Should.ThrowAsync<InvalidOperationException>(() => new RestoreArchive.Handler(
            Repository,
            Archive,
            Blob,
            Loggers.CreateLogger<RestoreArchive.Handler>())
            .Handle(new RestoreArchive.Request(hole, target.ConnectionString), Ct).AsTask());
    }

    [Fact]
    public async Task Restore_SilentlyDroppedEdge_RollsBack_And_LeavesTargetEmpty()
    {
        await SeedCorpusAsync();
        SnapshotCaptureResult captured = await Repository.CaptureAsync(ConnectionString, Ct);

        // An edge whose endpoint vertex is absent: its Cypher MATCH finds nothing, so the CREATE is a
        // silent no-op. Only a read-back reconciliation can see it; echoed counts would pass.
        var dangling = new SnapshotEdge(Guid.NewGuid(), Guid.NewGuid(), "depends_on", "reason");
        SnapshotCapture tampered = captured.Capture with { Edges = [.. captured.Capture.Edges, dangling] };
        SnapshotCounts expected = captured.Counts with { Edges = captured.Counts.Edges + 1 };

        await using SmoothAiProductContextMemoryTestDatabase target = await SmoothAiProductContextMemoryTestDatabase.CreateAsync(
            _aspire,
            $"infra-drop-{Guid.NewGuid():N}",
            Ct);
        await using NpgsqlDataSource targetDataSource = NpgsqlDataSourceFactory.Create(target.ConnectionString);
        await MigrateAsync(targetDataSource, Ct);

        RestoreResults restored = await Repository.RestoreAsync(
            target.ConnectionString, tampered, expected, overrideNonEmpty: false, Ct);

        restored.Committed.ShouldBeFalse();
        restored.Edges.ShouldBe(captured.Counts.Edges);
        (await Repository.IsTargetEmptyAsync(target.ConnectionString, Ct)).ShouldBeTrue();
    }

    private static void RewriteWithoutBlob(string sourcePath, string destPath)
    {
        Dictionary<string, byte[]> entries = ReadTar(sourcePath);
        string blob = entries.Keys.First(k => k.StartsWith("blobs/", StringComparison.Ordinal));
        entries.Remove(blob);
        using FileStream stream = File.Create(destPath);
        using var tar = new System.Formats.Tar.TarWriter(stream, System.Formats.Tar.TarEntryFormat.Ustar, leaveOpen: false);
        foreach ((string name, byte[] content) in entries)
        {
            tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(content, writable: false),
            });
        }
    }

    private static Dictionary<string, byte[]> ReadTar(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var tar = new System.Formats.Tar.TarReader(stream);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (tar.GetNextEntry() is { } entry)
        {
            using var buffer = new MemoryStream();
            entry.DataStream?.CopyTo(buffer);
            entries[entry.Name] = buffer.ToArray();
        }

        return entries;
    }

    [Fact]
    public async Task Restore_RefusesNonEmptyTarget_WithoutOverride()
    {
        await SeedCorpusAsync();
        SnapshotStore.Response snapshot = await new SnapshotStore.Handler(
            Repository,
            Archive,
            Blob,
            new FileSnapshotMetadataStore(CreateMetadataOptions()),
            Loggers.CreateLogger<SnapshotStore.Handler>())
            .Handle(new SnapshotStore.Request(ConnectionString, Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.tar")), Ct);

        // The same DB still holds rows — restore into it must refuse without the override flag.
        await Should.ThrowAsync<InvalidOperationException>(() => new RestoreArchive.Handler(
            Repository,
            Archive,
            Blob,
            Loggers.CreateLogger<RestoreArchive.Handler>())
            .Handle(new RestoreArchive.Request(snapshot.DestinationPath, ConnectionString, OverrideNonEmpty: false), Ct).AsTask());
    }

    private async Task<(int Memories, int Versions, int Vertices, int Edges, int TicketVertices, int TicketEdges, string Address)> SeedCorpusAsync()
    {
        var group = TestEntities.NewGroup(
            scopeDimension: MemoryGroup.ScopeDimensionValue.Product,
            tickets: [TicketDocument.Create(Provider, "PARENT", "https://tracker/p"), TicketDocument.Create(Provider, "CHILD", "https://tracker/c")]);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var memoryA = TestEntities.NewMemory(group.Id, "alpha", "alpha subject");
        var memoryB = TestEntities.NewMemory(group.Id, "beta", "beta subject");
        Db.Memories.AddRange(memoryA, memoryB);
        await Db.SaveChangesAsync(Ct);

        byte[] body = System.Text.Encoding.UTF8.GetBytes("round trip body payload");
        string address = await Blob.StoreAsync(new MemoryStream(body, writable: false), cancellationToken: Ct);

        MemoryVersion currentA = TestEntities.NewVersion(memoryA.Id, 2, "current claim", isCurrent: true);
        currentA.BlobAddress = address;
        Db.MemoryVersions.Add(TestEntities.NewVersion(memoryA.Id, 1, "first claim", isCurrent: false));
        Db.MemoryVersions.Add(currentA);
        Db.MemoryVersions.Add(TestEntities.NewVersion(memoryB.Id, 1, "beta claim", isCurrent: true));
        await Db.SaveChangesAsync(Ct);

        await new NpgsqlMemoryGraph(Db).CreateAsync(memoryA.Uuid, memoryB.Uuid, MemoryRelation.DependsOn, "reason", Ct);
        // Fresh hierarchy: CHILD has no parent yet, so ExpectedParent is null (not PARENT).
        await new NpgsqlTicketGraph(Db).ChangeParentAsync(
            new TicketParentChange(new(Provider, "CHILD"), new(Provider, "PARENT"), null, "depends_on", "test"), Ct);

        int edges = (int)await ScalarAsync(Db, "SELECT count(*) FROM memory_graph.\"LINKS\"", Ct);
        int ticketEdges = (int)await ScalarAsync(Db, "SELECT count(*) FROM memory_graph.\"TICKET_PARENT\"", Ct);
        return (2, 3, 2, edges, 2, ticketEdges, address);
    }

    private static async Task<long> ScalarAsync(SmoothAiProductContextMemoryDbContext db, string sql, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using NpgsqlCommand command = new(sql, (NpgsqlConnection)db.Database.GetDbConnection());
        object? result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    private static Microsoft.Extensions.Options.IOptions<SnapshotMetadataOptions> CreateMetadataOptions() =>
        Microsoft.Extensions.Options.Options.Create(new SnapshotMetadataOptions { Directory = Path.GetTempPath() });

    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddDbContext<SmoothAiProductContextMemoryDbContext>(options =>
            options.UseNpgsql(dataSource, npgsql => npgsql.UseSmoothAiProductContextMemoryHistory()));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.MigrateSmoothAiProductContextMemoryAsync(cancellationToken);
    }
}
