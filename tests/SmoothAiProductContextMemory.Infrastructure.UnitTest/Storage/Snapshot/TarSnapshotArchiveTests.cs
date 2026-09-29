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
    {
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);

        SnapshotWriteReport report = await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

        SnapshotVerification verification = await _archive.VerifyAsync(path, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeTrue();
        verification.Findings.ShouldBeEmpty();

        SnapshotCapture roundTrip = (await _archive.ReadAsync(path, TestContext.Current.CancellationToken)).Capture;
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
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

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

        SnapshotWriteReport report = await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((actual, Sha256ContentAddress.Hash(actual), (string?)null)), TestContext.Current.CancellationToken);
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
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

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
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

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
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

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
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((actual, Sha256ContentAddress.Hash(actual), (string?)null)), TestContext.Current.CancellationToken);

        SnapshotVerification verification = await _archive.VerifyAsync(path, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldContain(f => f.Kind == SnapshotFindingKind.CaptureTimeDefect);
    }

    [Fact]
    public async Task ReadAsync_Refuses_Unsupported_FormatVersion_Before_Materialising()
    {
        // ReadAsync gates on the manifest format so a newer-format archive is refused before its
        // member bytes are deserialised. The capture that restore reads is the one on the opened
        // archive, so it is refused through the same gate.
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

        Dictionary<string, byte[]> entries = ReadTar(path);
        SnapshotManifest manifest = JsonSerializer.Deserialize<SnapshotManifest>(entries[SnapshotEntryNames.Manifest], Json)!;
        entries[SnapshotEntryNames.Manifest] = JsonSerializer.SerializeToUtf8Bytes(
            manifest with { FormatVersion = SnapshotFormat.Version + 1 });
        string newer = TempArchive();
        WriteTar(newer, entries);

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
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

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
    public async Task Verify_Reports_LegacyFormatVersion_As_NotClean()
    {
        // C1: the format version was bumped when DanglingReferences/MismatchedBodies added refusal
        // semantics. An archive stamped with a pre-bump version (field absent → 0) must be refused
        // as unsupported, not reported clean. This is the VerifyAsync finding branch (not the throw
        // path exercised by ReadAsync_Refuses_Unsupported_FormatVersion).
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

        Dictionary<string, byte[]> entries = ReadTar(path);
        SnapshotManifest manifest = JsonSerializer.Deserialize<SnapshotManifest>(entries[SnapshotEntryNames.Manifest], Json)!;
        entries[SnapshotEntryNames.Manifest] = JsonSerializer.SerializeToUtf8Bytes(manifest with { FormatVersion = 1 });
        string stale = TempArchive();
        WriteTar(stale, entries);

        SnapshotVerification verification = await _archive.VerifyAsync(stale, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldContain(f => f.Kind == SnapshotFindingKind.Corruption);
    }

    [Fact]
    public async Task Verify_Reports_NullManifest_As_NotClean_WithoutThrowing()
    {
        // C2: the manifest member bytes being the literal `null` make Deserialize<T> throw
        // InvalidDataException, not JsonException. Verify must map that to a finding, not a stack
        // trace — "verify reports, it never throws".
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

        Dictionary<string, byte[]> entries = ReadTar(path);
        entries[SnapshotEntryNames.Manifest] = Encoding.UTF8.GetBytes("null");
        string tampered = TempArchive();
        WriteTar(tampered, entries);

        SnapshotVerification verification = await _archive.VerifyAsync(tampered, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Verify_Detects_MissingManifestEntry()
    {
        // T3: removing the manifest member itself must be a finding, not an exception.
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

        Dictionary<string, byte[]> entries = ReadTar(path);
        entries.Remove(SnapshotEntryNames.Manifest);
        string tampered = TempArchive();
        WriteTar(tampered, entries);

        SnapshotVerification verification = await _archive.VerifyAsync(tampered, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldContain(f => f.Kind == SnapshotFindingKind.MissingEntry);
    }

    [Fact]
    public async Task Verify_Detects_AnArchiveMemberTheManifestDoesNotList()
    {
        // UnexpectedEntry had no assertion and no test even built the shape it exists for: a member in
        // the container that the manifest does not list. Every tamper case either mutated a listed
        // member or removed one, so the surplus-member branch was dead as far as the suite could see.
        // It is the branch that catches a member *added* to the archive — a tampered payload the
        // manifest never accounted for, which no count comparison would notice.
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

        Dictionary<string, byte[]> entries = ReadTar(path);
        entries["blobs/smuggled"] = "not what the manifest promised"u8.ToArray();
        string tampered = TempArchive();
        WriteTar(tampered, entries);

        SnapshotVerification verification = await _archive.VerifyAsync(tampered, TestContext.Current.CancellationToken);

        verification.IsClean.ShouldBeFalse();
        SnapshotFinding finding = verification.Findings
            .Single(f => f.Kind == SnapshotFindingKind.UnexpectedEntry);
        finding.EntryName.ShouldBe("blobs/smuggled");
    }

    [Fact]
    public async Task Verify_Reports_NullEntriesOrCounts_As_NotClean()
    {
        // T3: a manifest with a null entry list or corpus counts must be rejected, not dereferenced
        // with a NullReferenceException escaping the verifier.
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

        Dictionary<string, byte[]> entries = ReadTar(path);
        SnapshotManifest manifest = JsonSerializer.Deserialize<SnapshotManifest>(entries[SnapshotEntryNames.Manifest], Json)!;
        entries[SnapshotEntryNames.Manifest] = JsonSerializer.SerializeToUtf8Bytes(manifest with { Entries = null! });
        string tampered = TempArchive();
        WriteTar(tampered, entries);

        SnapshotVerification verification = await _archive.VerifyAsync(tampered, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldContain(f => f.Kind == SnapshotFindingKind.Corruption);
    }

    [Fact]
    public async Task Verify_Reports_TruncatedMember_As_NotClean_WithoutThrowing()
    {
        // Every other tamper case here rewrites a well-formed tar, so the archive *container* was
        // never exercised -- and the container is the one layer a half-copied or truncated archive
        // fails at. The header declares the full member size and the file carries fewer bytes, which
        // is what truncation looks like on disk and is independent of the writer's block padding.
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);
        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

        Dictionary<string, byte[]> entries = ReadTar(path);
        string truncated = TempArchive();
        // Written by the real writer, then cut inside the final member's body. Building the tar by
        // hand would test this handler's header parser rather than its truncation handling, and a
        // malformed header fails the same way a truncated one does -- for the wrong reason.
        byte[] bytes = File.ReadAllBytes(path);
        int lastMemberDataStart = LastMemberDataOffset(bytes);
        await File.WriteAllBytesAsync(truncated, bytes.AsSpan(0, lastMemberDataStart + 16).ToArray(), TestContext.Current.CancellationToken);

        SnapshotVerification verification = await _archive.VerifyAsync(truncated, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Verify_Reports_NonTarFile_As_NotClean_WithoutThrowing()
    {
        // A file that is not a tar at all -- the wrong path, a text file, an empty capture.
        string notATar = TempArchive();
        await File.WriteAllTextAsync(notATar, "this is not a tar archive, it is just some text", TestContext.Current.CancellationToken);

        SnapshotVerification verification = await _archive.VerifyAsync(notATar, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Verify_Reports_MissingArchiveFile_As_NotClean_WithoutThrowing()
    {
        // The file verify was pointed at does not exist. It is a defect to report, not an unhandled
        // FileNotFoundException -- otherwise the operator sees a stack trace instead of the answer.
        string absent = Path.Combine(Path.GetDirectoryName(TempArchive())!, "never-written.tar");

        SnapshotVerification verification = await _archive.VerifyAsync(absent, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Write_LeavesNoTempFile_WhenTheBlobReadFails()
    {
        // The temp-then-move guarantees the *destination* is never a partial archive, which is what
        // it is for. It says nothing about the temp path: on a failed write that file held the whole
        // corpus so far, under a name derived from the destination, and nothing cleaned it up.
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);

        await Should.ThrowAsync<InvalidOperationException>(() => _archive.WriteAsync(
            path,
            capture,
            walk,
            _ => throw new InvalidOperationException("blob store unavailable"),
            TestContext.Current.CancellationToken));

        File.Exists(path + ".tmp").ShouldBeFalse();
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task Write_RemovesTheTempFile_OnSuccessToo()
    {
        // The success path moves the temp file, so nothing is left to delete -- asserted so the
        // cleanup cannot regress into one that only runs on failure.
        string path = TempArchive();
        string address = Sha256ContentAddress.Compute(Body);
        (SnapshotCapture capture, SnapshotWalkResult walk) = Capture(address, SnapshotBlobState.Ok);

        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

        File.Exists(path).ShouldBeTrue();
        File.Exists(path + ".tmp").ShouldBeFalse();
        Directory.GetFiles(Path.GetDirectoryName(path)!).ShouldNotContain(path + ".tmp");
    }

    [Fact]
    public async Task Verify_Reports_EmptyFile_As_NotClean_WithoutThrowing()
    {
        string empty = TempArchive();
        await File.WriteAllBytesAsync(empty, [], TestContext.Current.CancellationToken);

        SnapshotVerification verification = await _archive.VerifyAsync(empty, TestContext.Current.CancellationToken);
        verification.IsClean.ShouldBeFalse();
        verification.Findings.ShouldNotBeEmpty();
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

        await _archive.WriteAsync(path, capture, walk, _ => Task.FromResult((Body, Sha256ContentAddress.Hash(Body), (string?)null)), TestContext.Current.CancellationToken);

        SnapshotCapture round = (await _archive.ReadAsync(path, TestContext.Current.CancellationToken)).Capture;
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

    /// <summary>
    /// Byte offset at which the last member's body begins, located by walking the real headers. Used
    /// to cut the file inside a member rather than in tar's trailing zero blocks, so the truncation
    /// removes data instead of padding.
    /// </summary>
    private static int LastMemberDataOffset(byte[] tarBytes)
    {
        using var stream = new MemoryStream(tarBytes, writable: false);
        using var tar = new TarReader(stream);
        int offset = 0;
        int lastHeader = 0;
        while (tar.GetNextEntry() is { } entry)
        {
            lastHeader = offset;
            offset += 512 + (int)Math.Ceiling(entry.DataStream!.Length / 512d) * 512;
        }

        return lastHeader + 512;
    }
}
