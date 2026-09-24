using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.Storage.Snapshot;

/// <summary>
/// Writes and reads the corpus snapshot archive as a single tar: one entry per member (relational
/// capture, graph capture, each referenced blob body) plus the self-verifying manifest. The
/// manifest is written last and hashed, so verify can recompute every entry hash independently of
/// the manifest (LADR-02). Blob bodies with a non-<see cref="SnapshotBlobState.Ok"/> state are
/// recorded in the manifest/report but are not written — a mismatched or missing body cannot be
/// faithfully archived, and that is a store investigation, not a snapshot do-over.
/// </summary>
public sealed class TarSnapshotArchive : ISnapshotArchive
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SnapshotWriteReport> WriteAsync(
        string destinationPath,
        SnapshotCapture capture,
        SnapshotWalkResult walk,
        Func<string, Task<byte[]>> readBlobAsync,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath)) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        await using FileStream stream = File.Create(destinationPath);
        using var tar = new TarWriter(stream, TarEntryFormat.Ustar, leaveOpen: false);

        var entries = new List<SnapshotArchiveEntry>();
        var writeEntry = async (string name, byte[] content) =>
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(content, writable: false),
            });
            entries.Add(new SnapshotArchiveEntry(name, Sha256ContentAddress.Hash(content), content.Length));
        };

        await writeEntry(SnapshotEntryNames.Initiatives, Serialize(capture.Initiatives));
        await writeEntry(SnapshotEntryNames.Labels, Serialize(capture.Labels));
        await writeEntry(SnapshotEntryNames.MemoryGroups, Serialize(capture.MemoryGroups));
        await writeEntry(SnapshotEntryNames.GroupDescriptions, Serialize(capture.GroupDescriptions));
        await writeEntry(SnapshotEntryNames.Memories, Serialize(capture.Memories));
        await writeEntry(SnapshotEntryNames.MemoryVersions, Serialize(capture.MemoryVersions));
        await writeEntry(SnapshotEntryNames.Vertices, Serialize(capture.Vertices));
        await writeEntry(SnapshotEntryNames.Edges, Serialize(capture.Edges));
        await writeEntry(SnapshotEntryNames.TicketVertices, Serialize(capture.TicketVertices));
        await writeEntry(SnapshotEntryNames.TicketEdges, Serialize(capture.TicketEdges));

        int objects = 0;
        int mismatched = 0;
        int dangling = 0;
        foreach (SnapshotBlob blob in walk.Blobs)
        {
            switch (blob.State)
            {
                case SnapshotBlobState.Ok:
                    byte[] body = await readBlobAsync(blob.Address);
                    await writeEntry(BlobEntryName(blob.Address), body);
                    objects++;
                    break;
                case SnapshotBlobState.Mismatch:
                    // Faithfully archived under the cited address so verify can report the
                    // capture-time inconsistency distinctly from transit corruption (LADR-02).
                    byte[] mismatchedBody = await readBlobAsync(blob.Address);
                    await writeEntry(BlobEntryName(blob.Address), mismatchedBody);
                    objects++;
                    mismatched++;
                    break;
                case SnapshotBlobState.Missing:
                    dangling++;
                    break;
            }
        }

        var counts = new SnapshotCounts(
            capture.Memories.Count,
            capture.MemoryVersions.Count,
            capture.Vertices.Count,
            capture.Edges.Count,
            objects,
            capture.TicketVertices.Count,
            capture.TicketEdges.Count);

        var exclusions = new SnapshotExclusions(["recall_feedback"]);

        SnapshotManifest manifest = new(SnapshotFormat.Version, entries, counts, exclusions);
        await writeEntry(SnapshotEntryNames.Manifest, Serialize(manifest));

        int unreferenced = walk.UnreferencedObjects;

        return new SnapshotWriteReport(
            destinationPath,
            counts,
            dangling,
            unreferenced,
            mismatched);
    }

    public Task<SnapshotArchive> ReadAsync(string archivePath, CancellationToken cancellationToken)
    {
        Dictionary<string, byte[]> entries = ReadEntries(archivePath);

        if (!entries.TryGetValue(SnapshotEntryNames.Manifest, out byte[]? manifestBytes))
        {
            throw new InvalidDataException("Archive has no manifest.");
        }

        SnapshotManifest manifest = Deserialize<SnapshotManifest>(manifestBytes);
        string[] names = entries.Keys.ToArray();

        return Task.FromResult(new SnapshotArchive(manifest, names, name =>
        {
            if (!entries.TryGetValue(name, out byte[]? content))
            {
                throw new KeyNotFoundException($"Archive entry not found: {name}");
            }

            return Task.FromResult(content);
        }));
    }

    public Task<SnapshotVerification> VerifyAsync(string archivePath, CancellationToken cancellationToken)
    {
        Dictionary<string, byte[]> entries = ReadEntries(archivePath);
        var findings = new List<SnapshotFinding>();

        SnapshotManifest manifest;
        if (!entries.TryGetValue(SnapshotEntryNames.Manifest, out byte[]? manifestBytes))
        {
            findings.Add(new SnapshotFinding(SnapshotFindingKind.MissingEntry, SnapshotEntryNames.Manifest,
                "Manifest entry is missing from the archive."));
            return Task.FromResult(new SnapshotVerification(false, findings));
        }

        try
        {
            manifest = Deserialize<SnapshotManifest>(manifestBytes);
        }
        catch (JsonException)
        {
            findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, SnapshotEntryNames.Manifest,
                "Manifest is not valid JSON."));
            return Task.FromResult(new SnapshotVerification(false, findings));
        }

        Dictionary<string, SnapshotArchiveEntry> byName = manifest.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);

        // Every manifest entry must be present and hash-identical in the archive.
        foreach (SnapshotArchiveEntry expected in manifest.Entries)
        {
            if (!entries.TryGetValue(expected.Name, out byte[]? content))
            {
                findings.Add(new SnapshotFinding(SnapshotFindingKind.MissingEntry, expected.Name,
                    "Manifest entry is absent from the archive."));
                continue;
            }

            if (!string.Equals(Sha256ContentAddress.Hash(content), expected.Sha256, StringComparison.Ordinal))
            {
                findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, expected.Name,
                    "Entry content hash does not match the manifest."));
            }

            if (TryBlobAddress(expected.Name, out string? address)
                && !string.Equals(Sha256ContentAddress.Compute(content), address, StringComparison.Ordinal))
            {
                findings.Add(new SnapshotFinding(SnapshotFindingKind.CaptureTimeInconsistency, expected.Name,
                    "Blob content hash disagrees with the database-cited address (capture-time cross-store inconsistency)."));
            }
        }

        // Any archive member not listed in the manifest is unexpected (e.g. a removed-entry tamper).
        foreach (string name in entries.Keys)
        {
            if (name != SnapshotEntryNames.Manifest && !byName.ContainsKey(name))
            {
                findings.Add(new SnapshotFinding(SnapshotFindingKind.UnexpectedEntry, name,
                    "Archive member is not listed in the manifest."));
            }
        }

        ReconcileCounts(manifest, entries, findings);

        return Task.FromResult(new SnapshotVerification(findings.Count == 0, findings));
    }

    public Task<SnapshotCapture> ReadCaptureAsync(string archivePath, CancellationToken cancellationToken)
    {
        Dictionary<string, byte[]> entries = ReadEntries(archivePath);

        var capture = new SnapshotCapture(
            Deserialize<Initiative[]>(Require(entries, SnapshotEntryNames.Initiatives)),
            Deserialize<Label[]>(Require(entries, SnapshotEntryNames.Labels)),
            Deserialize<MemoryGroup[]>(Require(entries, SnapshotEntryNames.MemoryGroups)),
            Deserialize<GroupDescription[]>(Require(entries, SnapshotEntryNames.GroupDescriptions)),
            Deserialize<Memory[]>(Require(entries, SnapshotEntryNames.Memories)),
            Deserialize<MemoryVersion[]>(Require(entries, SnapshotEntryNames.MemoryVersions)),
            Deserialize<SnapshotVertex[]>(Require(entries, SnapshotEntryNames.Vertices)),
            Deserialize<SnapshotEdge[]>(Require(entries, SnapshotEntryNames.Edges)),
            Deserialize<SnapshotTicketVertex[]>(Require(entries, SnapshotEntryNames.TicketVertices)),
            Deserialize<SnapshotTicketEdge[]>(Require(entries, SnapshotEntryNames.TicketEdges)));

        return Task.FromResult(capture);
    }

    private static byte[] Require(IReadOnlyDictionary<string, byte[]> entries, string name) =>
        entries.TryGetValue(name, out byte[]? content)
            ? content
            : throw new InvalidDataException($"Archive is missing required entry: {name}");

    public Task<byte[]> ReadBlobAsync(string archivePath, string address, CancellationToken cancellationToken)
    {
        Dictionary<string, byte[]> entries = ReadEntries(archivePath);
        return Task.FromResult(Require(entries, BlobEntryName(address)));
    }

    private static void ReconcileCounts(
        SnapshotManifest manifest,
        IReadOnlyDictionary<string, byte[]> entries,
        ICollection<SnapshotFinding> findings)
    {
        int objects = 0;
        foreach (string name in entries.Keys)
        {
            if (TryBlobAddress(name, out _))
            {
                objects++;
            }
        }

        if (objects != manifest.Counts.Objects)
        {
            findings.Add(new SnapshotFinding(SnapshotFindingKind.CountMismatch, null,
                $"Object count in archive ({objects}) does not match the manifest ({manifest.Counts.Objects})."));
        }
    }

    private static Dictionary<string, byte[]> ReadEntries(string archivePath)
    {
        using FileStream stream = File.OpenRead(archivePath);
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

    private static bool TryBlobAddress(string entryName, out string address)
    {
        const string prefix = "blobs/";
        if (entryName.StartsWith(prefix, StringComparison.Ordinal))
        {
            address = entryName[prefix.Length..];
            return true;
        }

        address = string.Empty;
        return false;
    }

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);

    private static T Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Json)!;

    private static string BlobEntryName(string address) => $"blobs/{address}";
}
