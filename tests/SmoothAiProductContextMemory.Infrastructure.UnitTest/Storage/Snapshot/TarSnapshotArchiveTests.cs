using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Storage;
using SmoothAiProductContextMemory.Infrastructure.Storage.Snapshot;

namespace SmoothAiProductContextMemory.Infrastructure.UnitTest.Storage.Snapshot;

public class TarSnapshotArchiveTests
{
    private readonly TarSnapshotArchive _archive = new();
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("Document body without newlines.");

    // Matches the archive's own serialization (JsonSerializerDefaults.Web) so manifest
    // round-trips in tests interpret property names the same way the writer produced them.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task WrittenArchive_VerifiesClean_And_RoundTripsCapture()
    {        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);

        SnapshotWriteReport report = await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult(Body), TestContext.Current.CancellationToken);

        SnapshotVerification verification = await _archive.VerifyAsync(path, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeTrue();
        verification.Findings.ShouldBeEmpty();

        SnapshotCapture roundTrip = await _archive.ReadCaptureAsync(path, TestContext.Current.CancellationToken);
        roundTrip.Memories.Count.ShouldBe(capture.Memories.Count);
        roundTrip.MemoryVersions.Count.ShouldBe(capture.MemoryVersions.Count);
        roundTrip.Vertices.Count.ShouldBe(capture.Vertices.Count);
        roundTrip.Edges.Count.ShouldBe(capture.Edges.Count);
        report.Counts.Objects.ShouldBe(1);
    }

    [Fact]
    public async Task Verify_Detects_BlobCorruption()
    {
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult(Body), TestContext.Current.CancellationToken);

        Dictionary<string, byte[]> entries = ReadTar(path);
        entries[$"blobs/{address}"][0] ^= 0xFF; // flip one byte in the blob entry
        string tampered = TempArchive();
        WriteTar(tampered, entries);

        SnapshotVerification verification = await _archive.VerifyAsync(tampered, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Verify_Reports_CaptureTimeInconsistency_DistinctFromCorruption()
    {
        string path = TempArchive();
        byte[] actual = Encoding.UTF8.GetBytes("content that hashes elsewhere.");
        string cited = Sha256ContentAddress.Compute(Body); // cited address differs from actual content
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(cited, SnapshotBlobState.Mismatch);

        SnapshotWriteReport report = await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult(actual), TestContext.Current.CancellationToken);
        report.MismatchedBodies.ShouldBe(1);

        SnapshotVerification verification = await _archive.VerifyAsync(path, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldContain(f => f.Kind == SnapshotFindingKind.CaptureTimeInconsistency);
        verification.Findings.ShouldNotContain(f => f.Kind == SnapshotFindingKind.Corruption);
    }

    [Fact]
    public async Task Verify_Detects_MissingEntry()
    {
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult(Body), TestContext.Current.CancellationToken);

        Dictionary<string, byte[]> entries = ReadTar(path);
        entries.Remove($"blobs/{address}");
        string tampered = TempArchive();
        WriteTar(tampered, entries);

        SnapshotVerification verification = await _archive.VerifyAsync(tampered, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldContain(f => f.Kind == SnapshotFindingKind.MissingEntry);
    }

    [Fact]
    public async Task Verify_Detects_AlteredManifest()
    {
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult(Body), TestContext.Current.CancellationToken);

        Dictionary<string, byte[]> entries = ReadTar(path);
        entries[SnapshotEntryNames.Manifest][0] ^= 0xFF;
        string tampered = TempArchive();
        WriteTar(tampered, entries);

        SnapshotVerification verification = await _archive.VerifyAsync(tampered, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
    }

    [Fact]
    public async Task Verify_Reports_DanglingReferences_As_NotClean()
    {
        // A dangling (Missing) blob is never archived, so by itself verify would report the archive
        // clean. The manifest now records the count and verify must surface it as a defect.
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Missing);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult(Body), TestContext.Current.CancellationToken);

        SnapshotVerification verification = await _archive.VerifyAsync(path, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldContain(f => f.Kind == SnapshotFindingKind.CaptureTimeDefect);
    }

    [Fact]
    public async Task Verify_Reports_MismatchedBodies_As_NotClean()
    {
        string path = TempArchive();
        byte[] actual = Encoding.UTF8.GetBytes("content that hashes elsewhere.");
        string cited = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(cited, SnapshotBlobState.Mismatch);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult(actual), TestContext.Current.CancellationToken);

        SnapshotVerification verification = await _archive.VerifyAsync(path, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldContain(f => f.Kind == SnapshotFindingKind.CaptureTimeDefect);
    }

    [Fact]
    public async Task ReadCaptureAsync_Refuses_Unsupported_FormatVersion_Before_Materialising()
    {
        // ReadAsync gates on the manifest format; ReadCaptureAsync must gate the same way so a
        // newer-format archive is refused before its member bytes are deserialised.
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult(Body), TestContext.Current.CancellationToken);

        Dictionary<string, byte[]> entries = ReadTar(path);
        SnapshotManifest manifest = JsonSerializer.Deserialize<SnapshotManifest>(entries[SnapshotEntryNames.Manifest], Json)!;
        entries[SnapshotEntryNames.Manifest] = JsonSerializer.SerializeToUtf8Bytes(
            manifest with { FormatVersion = SnapshotFormat.Version + 1 });
        string newer = TempArchive();
        WriteTar(newer, entries);

        await Should.ThrowAsync<InvalidDataException>(async () =>
            await _archive.ReadCaptureAsync(newer, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<InvalidDataException>(async () =>
            await _archive.ReadAsync(newer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Verify_Detects_AlteredManifestCount()
    {
        // An altered manifest count on an untouched archive must be caught (R02). Verify reconciles
        // every counted corpus row against the archive, not just the blob/object count.
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult(Body), TestContext.Current.CancellationToken);

        Dictionary<string, byte[]> entries = ReadTar(path);
        SnapshotManifest manifest = JsonSerializer.Deserialize<SnapshotManifest>(entries[SnapshotEntryNames.Manifest], Json)!;
        entries[SnapshotEntryNames.Manifest] = JsonSerializer.SerializeToUtf8Bytes(
            manifest with { Counts = manifest.Counts with { Memories = 99999 } });
        string tampered = TempArchive();
        WriteTar(tampered, entries);

        SnapshotVerification verification = await _archive.VerifyAsync(tampered, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldContain(f => f.Kind == SnapshotFindingKind.CountMismatch);
    }

    [Fact]
    public async Task TicketEdges_RoundTrip_PreservingParentChildDirection()
    {
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        var capture = new SnapshotCapture(
            [], [], [], [], [], [], [], [],
            [
                new SnapshotTicketVertex("jira", "PARENT"),
                new SnapshotTicketVertex("jira", "CHILD"),
            ],
            [
                new SnapshotTicketEdge("jira", "CHILD", "jira", "PARENT", "depends_on", "api", DateTimeOffset.UtcNow, null),
            ]);
        var walk = new SnapshotWalkResult([new SnapshotBlob(address, SnapshotBlobState.Ok)], 0, 0);

        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult(Body), TestContext.Current.CancellationToken);

        SnapshotCapture round = await _archive.ReadCaptureAsync(path, TestContext.Current.CancellationToken);
        round.TicketEdges.Count.ShouldBe(1);
        round.TicketEdges[0].Key.ShouldBe("CHILD");
        round.TicketEdges[0].ParentKey.ShouldBe("PARENT");
    }

    private static (SnapshotCapture, SnapshotWalkResult) Capture(string address, SnapshotBlobState state)
    {
        var initiative = new Initiative { Id = 1, Name = "init", Description = "d", Status = "active" };
        var label = new Label { Id = 1, Name = "label", Status = "active" };
        var group = new MemoryGroup { Id = 1, Uuid = Guid.NewGuid(), ScopeDimension = "product", InitiativeId = 1, CreatedOn = DateTimeOffset.UtcNow };
        var description = new GroupDescription { Id = 1, GroupId = 1, Version = 1, Name = "g", Body = "b", CreatedOn = DateTimeOffset.UtcNow };
        var memory = new Memory { Id = 1, Uuid = Guid.NewGuid(), LineageId = Guid.NewGuid(), GroupId = 1, Name = "m", Description = "d", SubjectSlug = "d" };
        var version = new MemoryVersion { Id = 1, MemoryId = 1, Version = 1, IsCurrent = true, Statement = "s", ContentSummary = "c", BlobAddress = address, Kind = "decision", Confidence = 5, Status = "approved", ValidFrom = DateTimeOffset.UtcNow, CreatedOn = DateTimeOffset.UtcNow };

        var capture = new SnapshotCapture(
            [initiative],
            [label],
            [group],
            [description],
            [memory],
            [version],
            [new SnapshotVertex(memory.Uuid)],
            [new SnapshotEdge(memory.Uuid, memory.Uuid, "relates_to", "reason")],
            [],
            []);

        var walk = new SnapshotWalkResult([new SnapshotBlob(address, state)], state == SnapshotBlobState.Missing ? 1 : 0, 0);
        return (capture, walk);
    }

    private static string TempArchive()
    {
        string dir = Path.Combine(Path.GetTempPath(), "snap-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "snapshot.tar");
    }

    private static Dictionary<string, byte[]> ReadTar(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var tar = new TarReader(stream);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (tar.GetNextEntry() is { } entry)
        {
            using var buffer = new MemoryStream();
            entry.DataStream?.CopyTo(buffer);
            entries[entry.Name] = buffer.ToArray();
        }

        return entries;
    }

    private static void WriteTar(string path, IReadOnlyDictionary<string, byte[]> entries)
    {
        using FileStream stream = File.Create(path);
        using var tar = new TarWriter(stream, TarEntryFormat.Ustar, leaveOpen: false);
        foreach ((string name, byte[] content) in entries)
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(content, writable: false),
            });
        }
    }
}
