using NodaTime;
using SmoothAiProductContextMemory.Application.Common.Serialization;
using System.Formats.Tar;
using System.Text.Json;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Infrastructure.Storage;
using SmoothAiProductContextMemory.Infrastructure.Storage.Snapshot;

namespace SmoothAiProductContextMemory.Infrastructure.UnitTest.Storage.Snapshot;

public sealed class OperationReceiptArchiveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mimi-receipt-archive-" + Guid.NewGuid().ToString("N"));
    private readonly TarSnapshotArchive _archive = new();
    private static readonly JsonSerializerOptions Json = InstantJson.Configure(new(JsonSerializerDefaults.Web));
    private static readonly OperationReceipt Receipt = new("capture:1:commit", new string('a', 64), "memories", "{\"created\":1}", Instant.FromUtc(2026, 10, 3, 0, 0));
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task V6_preserves_receipts_and_main_graph_members_together()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "combined.tar");
        var about = new SnapshotAboutEdge(Guid.NewGuid(), "synthetic", "T-1", "reference", Receipt.CommittedAt);
        var product = new SnapshotProductVertex("synthetic-product", "description", "active");
        var capture = new SnapshotCapture([], [], [], [], [], [], [], [], [], [],
            [about], [product], [new SnapshotNamedVertex("initiative")], [new SnapshotNamedVertex("repo")],
            [new SnapshotProductInitiativeEdge("synthetic-product", "initiative", "reason", "test", Receipt.CommittedAt)],
            [new SnapshotProductRepoEdge("synthetic-product", "repo", "reason", "test", Receipt.CommittedAt)], [Receipt]);
        await _archive.WriteAsync(path, capture, new SnapshotWalkResult([], 0, 0),
            (_, _) => throw new InvalidOperationException("No blobs expected."), Ct);
        (await _archive.VerifyAsync(path, Ct)).IsClean.ShouldBeTrue();
        var round = await _archive.ReadAsync(path, Ct);
        round.Manifest.FormatVersion.ShouldBe(6);
        round.Capture.OperationReceipts.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(Receipt);
        round.Capture.AboutEdges.ShouldHaveSingleItem().ShouldBe(about);
        round.Capture.ProductVertices.ShouldHaveSingleItem().ShouldBe(product);
        round.Capture.ProductInitiativeEdges.ShouldHaveSingleItem().ShouldBe(capture.ProductInitiativeEdges[0]);
        round.Capture.ProductRepoEdges.ShouldHaveSingleItem().ShouldBe(capture.ProductRepoEdges[0]);
    }

    [Fact]
    public async Task Receipts_are_hashed_counted_and_round_trip()
    {
        string path = await WriteAsync([Receipt]);
        (await _archive.VerifyAsync(path, Ct)).IsClean.ShouldBeTrue();
        var archive = await _archive.ReadAsync(path, Ct);
        archive.Manifest.Counts.OperationReceipts.ShouldBe(1);
        archive.Capture.OperationReceipts.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(Receipt);
        archive.Manifest.Entries.ShouldContain(e => e.Name == SnapshotEntryNames.OperationReceipts);
    }

    [Fact]
    public async Task Missing_receipt_member_is_refused_even_if_removed_from_manifest()
    {
        string path = await WriteAsync([Receipt]);
        Rewrite(path, members =>
        {
            members.Remove(SnapshotEntryNames.OperationReceipts);
            SnapshotManifest manifest = JsonSerializer.Deserialize<SnapshotManifest>(members[SnapshotEntryNames.Manifest], Json)!;
            members[SnapshotEntryNames.Manifest] = JsonSerializer.SerializeToUtf8Bytes(manifest with
            {
                Entries = manifest.Entries.Where(e => e.Name != SnapshotEntryNames.OperationReceipts).ToArray(),
                Counts = manifest.Counts with { OperationReceipts = 0 },
            }, Json);
        });
        (await _archive.VerifyAsync(path, Ct)).IsClean.ShouldBeFalse();
    }

    [Fact]
    public async Task Altered_receipt_bytes_fail_hash_verification()
    {
        string path = await WriteAsync([Receipt]);
        Rewrite(path, members => members[SnapshotEntryNames.OperationReceipts] = JsonSerializer.SerializeToUtf8Bytes(new[] { Receipt with { OperationKey = "different" } }, Json));
        (await _archive.VerifyAsync(path, Ct)).Findings.ShouldContain(f => f.Kind == SnapshotFindingKind.Corruption && f.EntryName == SnapshotEntryNames.OperationReceipts);
    }

    [Fact]
    public async Task Duplicate_receipt_keys_are_refused_with_consistent_hashes_and_counts()
    {
        string path = await WriteAsync([Receipt, Receipt]);
        (await _archive.VerifyAsync(path, Ct)).IsClean.ShouldBeFalse();
    }

    [Fact]
    public async Task Existing_v5_archive_remains_readable_with_empty_receipts()
    {
        string path = await WriteAsync([]);
        Rewrite(path, members =>
        {
            members.Remove(SnapshotEntryNames.OperationReceipts);
            SnapshotManifest manifest = JsonSerializer.Deserialize<SnapshotManifest>(members[SnapshotEntryNames.Manifest], Json)!;
            members[SnapshotEntryNames.Manifest] = JsonSerializer.SerializeToUtf8Bytes(manifest with
            {
                FormatVersion = 5,
                Entries = manifest.Entries.Where(e => e.Name != SnapshotEntryNames.OperationReceipts).ToArray(),
            }, Json);
        });
        (await _archive.VerifyAsync(path, Ct)).IsClean.ShouldBeTrue();
        (await _archive.ReadAsync(path, Ct)).Capture.OperationReceipts.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Pre_graph_formats_are_refused(int version)
    {
        string path = await WriteAsync([]);
        Rewrite(path, members =>
        {
            SnapshotManifest manifest = JsonSerializer.Deserialize<SnapshotManifest>(members[SnapshotEntryNames.Manifest], Json)!;
            members[SnapshotEntryNames.Manifest] = JsonSerializer.SerializeToUtf8Bytes(manifest with { FormatVersion = version }, Json);
        });
        (await _archive.VerifyAsync(path, Ct)).IsClean.ShouldBeFalse();
        await Should.ThrowAsync<InvalidDataException>(() => _archive.ReadAsync(path, Ct));
    }

    private async Task<string> WriteAsync(IReadOnlyList<OperationReceipt> receipts)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tar");
        var capture = new SnapshotCapture([], [], [], [], [], [], [], [], [], [], [], [], [], [], [], [], receipts);
        await _archive.WriteAsync(path, capture, new SnapshotWalkResult([], 0, 0),
            (_, _) => throw new InvalidOperationException("No blobs expected."), Ct);
        return path;
    }

    private static void Rewrite(string path, Action<Dictionary<string, byte[]>> change)
    {
        var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var reader = new TarReader(File.OpenRead(path)))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                using var buffer = new MemoryStream();
                entry.DataStream!.CopyTo(buffer);
                members.Add(entry.Name, buffer.ToArray());
            }
        }

        change(members);
        using var writer = new TarWriter(File.Create(path), leaveOpen: false);
        foreach ((string name, byte[] content) in members)
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content, false) });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
    }
}
