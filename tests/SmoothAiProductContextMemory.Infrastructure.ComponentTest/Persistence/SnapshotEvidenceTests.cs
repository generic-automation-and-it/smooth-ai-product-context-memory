using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Application.Features.Snapshot;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;
using SmoothAiProductContextMemory.Infrastructure.Storage;
using SmoothAiProductContextMemory.Infrastructure.Storage.Snapshot;
using SmoothAiProductContextMemory.TestFramework.Fixtures;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// NFR-04 evidence: the full-snapshot time at reference corpus volume. Env-gated because seeding
/// thousands of memories with blob bodies costs minutes and the output is evidence committed to the
/// HLD folder, not a pass/fail the gate needs on every push. Run with
/// <c>SMOOTH_SNAPSHOT_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest
/// --filter SnapshotEvidenceTests</c>. The measured single-digit-minute target (NFR-04) is asserted
/// as a loose ceiling so a regression is caught, with the real number recorded in the nfrs folder.
/// </summary>
public sealed class SnapshotEvidenceTests : PersistenceTestBase
{
    private const int MemoryCount = 1_000;

    private readonly AspireFixture _aspire;
    private S3BlobStorage? _blob;

    private S3BlobStorage Blob => _blob ??= new S3BlobStorage(
        Microsoft.Extensions.Options.Options.Create(new BlobStorageOptions
        {
            Endpoint = _aspire.BlobEndpoint,
            AccessKey = AspireFixture.BlobAccessKey,
            SecretKey = AspireFixture.BlobSecretKey,
            Bucket = $"snap-evidence-{Guid.NewGuid():N}",
        }),
        new TestHttpClientFactory(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<S3BlobStorage>.Instance);

    private ILoggerFactory Loggers => LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));

    public SnapshotEvidenceTests(AspireFixture aspire) : base(aspire)
    {
        _aspire = aspire;
    }

    [Fact]
    public async Task FullSnapshot_AtReferenceVolume_MeetsSingleDigitMinuteTarget()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable("SMOOTH_SNAPSHOT_BENCH"), "1", StringComparison.Ordinal),
            "Set SMOOTH_SNAPSHOT_BENCH=1 to record the NFR-04 snapshot measurement.");

        var group = TestEntities.NewGroup(scopeDimension: MemoryGroup.ScopeDimensionValue.Product);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var blobAddress = new HashSet<string>(StringComparer.Ordinal);
        var memories = new List<(Memory M, MemoryVersion V)>();
        var stopwatch = new Stopwatch();

        stopwatch.Start();
        for (int i = 0; i < MemoryCount; i++)
        {
            byte[] body = Encoding.UTF8.GetBytes($"dummy body for memory {i}");
            string address = await Blob.StoreAsync(new MemoryStream(body, writable: false), cancellationToken: Ct);
            blobAddress.Add(address);

            var memory = TestEntities.NewMemory(group.Id, $"memory-{i}", $"subject number {i}");
            Db.Memories.Add(memory);
            await Db.SaveChangesAsync(Ct);

            var version = TestEntities.NewVersion(memory.Id, 1, $"claim {i}", isCurrent: true);
            version.BlobAddress = address;
            Db.MemoryVersions.Add(version);
            memories.Add((memory, version));
        }

        await Db.SaveChangesAsync(Ct);
        var graph = new NpgsqlMemoryGraph(Db);
        for (int i = 1; i < memories.Count; i++)
        {
            await graph.CreateAsync(memories[i - 1].M.Uuid, memories[i].M.Uuid, MemoryRelation.RelatesTo, "ev", Ct);
        }

        double seedSeconds = stopwatch.Elapsed.TotalSeconds;
        stopwatch.Restart();

        SnapshotStore.Response snapshot = await new SnapshotStore.Handler(
            new NpgsqlSnapshotRepository(Blob, Blob),
            new TarSnapshotArchive(),
            Blob,
            new FileSnapshotMetadataStore(Microsoft.Extensions.Options.Options.Create(new SnapshotMetadataOptions { Directory = Path.GetTempPath() })),
            Loggers.CreateLogger<SnapshotStore.Handler>())
            .Handle(new SnapshotStore.Request(ConnectionString, Path.Combine(Path.GetTempPath(), $"evidence-{Guid.NewGuid():N}.tar")), Ct);

        double snapshotSeconds = stopwatch.Elapsed.TotalSeconds;

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"NFR-04 snapshot evidence: memories={snapshot.Memories} versions={snapshot.Versions} edges={snapshot.Edges} objects={snapshot.Objects}; seed={seedSeconds:F1}s snapshot={snapshotSeconds:F2}s ({(int)(snapshotSeconds / 60)}m{snapshotSeconds % 60:F0}s).");

        snapshot.Memories.ShouldBe(MemoryCount);
        snapshot.Objects.ShouldBe(blobAddress.Count);
        snapshot.Edges.ShouldBe(MemoryCount - 1);
        snapshotSeconds.ShouldBeLessThan(540.0); // single-digit-minute ceiling (NFR-04, ≤ 9m)
    }
}
