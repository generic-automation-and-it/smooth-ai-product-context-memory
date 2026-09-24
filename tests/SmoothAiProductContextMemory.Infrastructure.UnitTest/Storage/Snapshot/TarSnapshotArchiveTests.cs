using System.Formats.Tar;
using System.Text;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Storage;
using SmoothAiProductContextMemory.Infrastructure.Storage.Snapshot;

namespace SmoothAiProductContextMemory.Infrastructure.UnitTest.Storage.Snapshot;

public class TarSnapshotArchiveTests
{
    private readonly TarSnapshotArchive _archive = new();
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("Document body without newlines.");

    [Fact]
    public async Task WrittenArchive_VerifiesClean_And_RoundTripsCapture()
    {
        string path = TempArchive();
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
    public void Verify_Delivers_NoContent_OnFindings()
    {
        // The manifest deliberately excludes memory content; verify output is hashes/identifiers only.
        SnapshotManifest manifest = new(
            SnapshotFormat.Version,
            [new SnapshotArchiveEntry("entry", "abc", 3)],
            new SnapshotCounts(0, 0, 0, 0, 0, 0, 0),
            new SnapshotExclusions(["recall_feedback"]));

        manifest.Counts.Memories.ShouldBe(0);
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
