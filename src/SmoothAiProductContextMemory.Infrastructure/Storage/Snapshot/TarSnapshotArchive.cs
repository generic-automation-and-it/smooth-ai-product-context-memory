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

        SnapshotManifest manifest = new(SnapshotFormat.Version, entries, counts, exclusions, dangling, mismatched);
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

        SnapshotManifest manifest = ReadAndGateManifest(entries);

        string[] names = entries.Keys.ToArray();

        return Task.FromResult(new SnapshotArchive(
            manifest,
            names,
            name =>
            {
                if (!entries.TryGetValue(name, out byte[]? content))
                {
                    throw new KeyNotFoundException($"Archive entry not found: {name}");
                }

                return Task.FromResult(content);
            },
            address => entries.ContainsKey(BlobEntryName(address)),
            address => entries[BlobEntryName(address)]));
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

        if (manifest.FormatVersion != SnapshotFormat.Version)
        {
            findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, SnapshotEntryNames.Manifest,
                $"Archive format version {manifest.FormatVersion} is not supported (expected {SnapshotFormat.Version}); the member layout cannot be interpreted safely."));
            return Task.FromResult(new SnapshotVerification(false, findings));
        }

        // The manifest is the only unhashed member, so its entry list is trusted input: a repeated
        // name is tamper, not a duplicate to collapse. Report it rather than letting the duplicate
        // key throw out of verify — the same "reports, it never throws" rule the corrupt-member
        // branches in CheckCount follow.
        var byName = new Dictionary<string, SnapshotArchiveEntry>(StringComparer.Ordinal);
        foreach (SnapshotArchiveEntry entry in manifest.Entries)
        {
            if (!byName.TryAdd(entry.Name, entry))
            {
                findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, entry.Name,
                    "Manifest lists the same entry name more than once."));
            }
        }

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

        if (manifest.DanglingReferences > 0)
        {
            findings.Add(new SnapshotFinding(SnapshotFindingKind.CaptureTimeDefect, null,
                $"Archive recorded {manifest.DanglingReferences} dangling reference(s) at capture; those bodies were never archived, so this archive cannot restore cleanly."));
        }

        if (manifest.MismatchedBodies > 0)
        {
            findings.Add(new SnapshotFinding(SnapshotFindingKind.CaptureTimeDefect, null,
                $"Archive recorded {manifest.MismatchedBodies} mismatched blob body/bodies at capture; the stored body disagreed with the database-cited address."));
        }

        return Task.FromResult(new SnapshotVerification(findings.Count == 0, findings));
    }

    public Task<SnapshotCapture> ReadCaptureAsync(string archivePath, CancellationToken cancellationToken)
    {
        Dictionary<string, byte[]> entries = ReadEntries(archivePath);

        // Gate on the manifest format before materialising any member layout, exactly as ReadAsync
        // does, so a newer-format archive is refused before its member bytes are deserialised.
        _ = ReadAndGateManifest(entries);

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

    private static SnapshotManifest ReadAndGateManifest(IReadOnlyDictionary<string, byte[]> entries)
    {
        if (!entries.TryGetValue(SnapshotEntryNames.Manifest, out byte[]? manifestBytes))
        {
            throw new InvalidDataException("Archive has no manifest.");
        }

        SnapshotManifest manifest = Deserialize<SnapshotManifest>(manifestBytes);
        if (manifest.FormatVersion != SnapshotFormat.Version)
        {
            throw new InvalidDataException(
                $"Archive format version {manifest.FormatVersion} is not supported (expected {SnapshotFormat.Version}); restore is refused because a newer/older member layout cannot be interpreted safely.");
        }

        return manifest;
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
        // Every corpus count the manifest records must match what the archive actually holds; an
        // altered manifest count on an untouched archive must be caught here (R02).
        CheckCount(entries, SnapshotEntryNames.Memories, manifest.Counts.Memories, "memory", findings);
        CheckCount(entries, SnapshotEntryNames.MemoryVersions, manifest.Counts.Versions, "memory version", findings);
        CheckCount(entries, SnapshotEntryNames.Vertices, manifest.Counts.Vertices, "graph vertex", findings);
        CheckCount(entries, SnapshotEntryNames.Edges, manifest.Counts.Edges, "graph edge", findings);
        CheckCount(entries, SnapshotEntryNames.TicketVertices, manifest.Counts.TicketVertices, "ticket vertex", findings);
        CheckCount(entries, SnapshotEntryNames.TicketEdges, manifest.Counts.TicketEdges, "ticket edge", findings);

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

    private static void CheckCount(
        IReadOnlyDictionary<string, byte[]> entries,
        string entryName,
        int expected,
        string label,
        ICollection<SnapshotFinding> findings)
    {
        if (!entries.TryGetValue(entryName, out byte[]? content))
        {
            findings.Add(new SnapshotFinding(SnapshotFindingKind.CountMismatch, entryName,
                $"{label} entry is absent from the archive; the manifest claims {expected}."));
            return;
        }

        // The member entries are serialized JSON arrays; count the elements without binding to the
        // concrete entity shape so verify stays independent of the model. A member already flagged
        // corrupt above may not parse at all, so an unparseable or non-array member becomes a
        // finding naming it rather than an exception: verify reports, it never throws.
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, entryName,
                    $"{label} entry is not a JSON array, so its element count cannot be reconciled against the manifest."));
                return;
            }

            int actual = document.RootElement.GetArrayLength();
            if (actual != expected)
            {
                findings.Add(new SnapshotFinding(SnapshotFindingKind.CountMismatch, entryName,
                    $"{label} count in archive ({actual}) does not match the manifest ({expected})."));
            }
        }
        catch (JsonException)
        {
            findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, entryName,
                $"{label} entry is not valid JSON, so its element count cannot be reconciled against the manifest."));
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

    private static T Deserialize<T>(byte[] bytes) =>
        JsonSerializer.Deserialize<T>(bytes, Json)
        ?? throw new InvalidDataException($"Archive member '{typeof(T).Name}' deserialized to null.");

    private static string BlobEntryName(string address) => $"blobs/{address}";
}
