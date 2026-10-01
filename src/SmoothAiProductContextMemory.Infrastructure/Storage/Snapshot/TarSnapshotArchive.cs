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
        Func<string, CancellationToken, Task<(byte[] Content, string Sha256, string? ContentType)>> readBlobAsync,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath)) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        // Write to a temp path and move it into place only on success, so a failure mid-write leaves
        // no truncated archive at the destination. The move is an atomic *replace*, not a
        // refusal to overwrite: an existing archive at the destination is superseded only by a
        // complete write. Refusing a non-empty destination is a different guarantee, and the
        // Markdown export sink is where that one lives.
        string tempPath = destinationPath + ".tmp";
        SnapshotWriteReport report;
        FileStream? stream = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream = File.Create(tempPath);
            var tar = new TarWriter(stream, TarEntryFormat.Ustar, leaveOpen: true);

            var entries = new List<SnapshotArchiveEntry>();
            var writeEntry = async (string name, byte[] content, string? sha256 = null, string? contentType = null) =>
            {
                await tar.WriteEntryAsync(
                    new PaxTarEntry(TarEntryType.RegularFile, name)
                    {
                        DataStream = new MemoryStream(content, writable: false),
                    },
                    cancellationToken);
                entries.Add(new SnapshotArchiveEntry(name, sha256 ?? Sha256ContentAddress.Hash(content), content.Length, contentType));
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
                cancellationToken.ThrowIfCancellationRequested();
                switch (blob.State)
                {
                    case SnapshotBlobState.Ok:
                        (byte[] body, string bodySha256, string? bodyContentType) = await readBlobAsync(blob.Address, cancellationToken);
                        await writeEntry(BlobEntryName(blob.Address), body, bodySha256, bodyContentType);
                        objects++;
                        break;
                    case SnapshotBlobState.Mismatch:
                        // Faithfully archived under the cited address so verify can report the
                        // capture-time inconsistency distinctly from transit corruption (LADR-02).
                        (byte[] mismatchedBody, string mismatchedSha256, string? mismatchedContentType) = await readBlobAsync(blob.Address, cancellationToken);
                        await writeEntry(BlobEntryName(blob.Address), mismatchedBody, mismatchedSha256, mismatchedContentType);
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

            report = new SnapshotWriteReport(
                destinationPath,
                counts,
                dangling,
                unreferenced,
                mismatched);

            await tar.DisposeAsync();
            // The rename is atomic; the write is not durable until the data is on the device. Without
            // this a crash between the two leaves a correctly-named archive holding none of its
            // content -- and the per-member checksums that would catch it are inside it.
            stream.Flush(flushToDisk: true);
            await stream.DisposeAsync();
            stream = null;

            // Last point at which a cancel can still mean "no archive": after the move it exists.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }

            // A failed, cancelled or killed write leaves a content-bearing temp file beside the
            // destination, under a name derived from it, holding a second copy of the corpus that
            // nothing later cleans. On success the move has already consumed it, so this is a no-op.
            // A cleanup failure is swallowed so it cannot mask the failure that caused it.
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing actionable here: the original failure is the one worth reporting.
            }
        }

        return report;
    }

    public Task<SnapshotArchive> ReadAsync(string archivePath, CancellationToken cancellationToken)
    {
        (var entries, _) = ReadEntries(archivePath);

        SnapshotManifest manifest = ReadAndGateManifest(entries);
        SnapshotCapture capture = ReadCapture(entries);

        string[] names = entries.Keys.ToArray();

        // Lookup of a blob body's content type from the manifest entry. A v3 manifest records it per
        // blob; a missing/older entry resolves to null, which the restore handily stores as the
        // octet-stream default rather than dropping a value it never had.
        var contentTypes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (SnapshotArchiveEntry entry in manifest.Entries)
        {
            contentTypes.TryAdd(entry.Name, entry.ContentType);
        }

        return Task.FromResult(new SnapshotArchive(
            manifest,
            names,
            capture,
            address => entries.ContainsKey(BlobEntryName(address)),
            address => entries[BlobEntryName(address)],
            address => contentTypes.TryGetValue(BlobEntryName(address), out string? contentType) ? contentType : null));
    }

    public Task<SnapshotVerification> VerifyAsync(string archivePath, CancellationToken cancellationToken)
    {
        (var entries, var repeatedNames, var unreadable) = TryReadEntries(archivePath);
        var findings = new List<SnapshotFinding>();

        // Verify reports; it never throws. Every other failure mode here already resolves to a
        // finding, and a container that cannot be read at all -- truncated, not a tar, or absent --
        // is the case an operator most needs reported rather than thrown, since a stack trace says
        // nothing about which archive is unusable. ReadAsync still throws on the same input, because
        // a restore must refuse rather than restore half an archive.
        if (unreadable is not null)
        {
            findings.Add(unreadable);
            return Task.FromResult(new SnapshotVerification(false, findings));
        }

        // The archive-side twin of the manifest's duplicate-entry guard: every repeated member name
        // is a Corruption finding, so a duplicated member is reported rather than silently resolved
        // last-wins. Reported, never thrown.
        foreach (string repeated in repeatedNames)
        {
            findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, repeated,
                "Archive contains the same member name more than once."));
        }

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
        catch (InvalidDataException)
        {
            // Deserialize<T> throws InvalidDataException when the member bytes are the literal
            // `null` JSON value — not a JsonException, so it must be caught here or the verifier
            // escapes with a raw stack trace. Same "verify reports, it never throws" contract.
            findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, SnapshotEntryNames.Manifest,
                "Manifest is a null value, so it cannot be verified."));
            return Task.FromResult(new SnapshotVerification(false, findings));
        }

        if (manifest.FormatVersion != SnapshotFormat.Version)
        {
            findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, SnapshotEntryNames.Manifest,
                $"Archive format version {manifest.FormatVersion} is not supported (expected {SnapshotFormat.Version}); the member layout cannot be interpreted safely."));
            return Task.FromResult(new SnapshotVerification(false, findings));
        }

        // A tampered manifest can name the format version correctly while carrying an explicitly
        // null entry list or corpus count (an absent property deserialises the same way). Every
        // branch below dereferences both, so reject the shape here instead of letting a
        // NullReferenceException escape — verify reports, it never throws.
        if (manifest.Entries is null || manifest.Counts is null)
        {
            findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, SnapshotEntryNames.Manifest,
                "Manifest is missing its entry list or corpus counts, so it cannot be verified."));
            return Task.FromResult(new SnapshotVerification(false, findings));
        }

        // The manifest is the only unhashed member, so its entry list is trusted input: a repeated
        // name is tamper, not a duplicate to collapse. Report it rather than letting the duplicate
        // key throw out of verify — the same "reports, it never throws" rule the corrupt-member
        // branches in CheckCount follow.
        //
        // Two nulls are guarded here because both reach the dictionary and both would leave verify as
        // a stack trace rather than a report: a JSON null element deserialises to a null
        // SnapshotArchiveEntry, and an absent or explicitly null `Name` deserialises to a null string,
        // which Dictionary.TryAdd rejects with ArgumentNullException. Neither is a shape this writer
        // ever produces, which is exactly why the archive's own bytes cannot be assumed to.
        var byName = new Dictionary<string, SnapshotArchiveEntry>(StringComparer.Ordinal);
        foreach (SnapshotArchiveEntry? entry in manifest.Entries)
        {
            if (entry is null)
            {
                findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, SnapshotEntryNames.Manifest,
                    "Manifest lists a null entry, so the archive's member list cannot be interpreted."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, SnapshotEntryNames.Manifest,
                    "Manifest lists an entry with no name, so it cannot be matched to an archive member."));
                continue;
            }

            if (!byName.TryAdd(entry.Name, entry))
            {
                findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, entry.Name,
                    "Manifest lists the same entry name more than once."));
            }
        }

        // Every manifest entry must be present and hash-identical in the archive.
        foreach (SnapshotArchiveEntry expected in manifest.Entries)
        {
            if (expected is null || string.IsNullOrWhiteSpace(expected.Name))
            {
                // Already reported above; the loops below dereference the name, so they must not see it.
                continue;
            }

            if (!SnapshotContentTypes.IsAllowed(expected.ContentType))
            {
                // The type is restored verbatim as the object's Content-Type, and the manifest is not
                // hash-declared, so an unrecognised value is tamper rather than data. The value itself
                // is not echoed: it is attacker-shaped text in an operator report.
                findings.Add(new SnapshotFinding(SnapshotFindingKind.Corruption, expected.Name,
                    "Manifest records a content type this system never writes, or one that is malformed."));
            }

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
        CheckCitedBodiesPresent(entries, findings);

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

    private static SnapshotCapture ReadCapture(IReadOnlyDictionary<string, byte[]> entries) =>
        new(
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

    private static void ReconcileCounts(
        SnapshotManifest manifest,
        IReadOnlyDictionary<string, byte[]> entries,
        ICollection<SnapshotFinding> findings)
    {
        // Every corpus count the manifest records must match what the archive actually holds; an
        // altered manifest count on an untouched archive must be caught here.
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

    /// <summary>
    /// Every blob body a stored version cites must be in the archive under its content address.
    /// </summary>
    /// <remarks>
    /// The other checks all work from the manifest, and the manifest is the one member that is not
    /// hash-declared — so a body can be dropped from the archive and its line removed from the
    /// manifest, the object count adjusted, and the dangling-reference counter set to zero, and verify
    /// still reports clean. Nothing else looks at the relationship between the capture and the bodies,
    /// which is the relationship a restore depends on: the restore refuses a capture citing an absent
    /// body, so an archive that passes verify and then refuses to restore is a contradiction between
    /// the two halves of the same tool. A genuine capture-time dangling reference is still counted in
    /// the manifest and still reported; this adds the independent check the counter cannot be.
    ///
    /// The addresses are read out of the raw JSON rather than by binding the version model, for the
    /// same reason CheckCount does: verify must not depend on the shape it is verifying.
    /// </summary>
    private static void CheckCitedBodiesPresent(
        IReadOnlyDictionary<string, byte[]> entries,
        ICollection<SnapshotFinding> findings)
    {
        if (!entries.TryGetValue(SnapshotEntryNames.MemoryVersions, out byte[]? content))
        {
            // Already a CountMismatch finding naming the absent member. Nothing to cross-check.
            return;
        }

        HashSet<string> present = new(StringComparer.Ordinal);
        foreach (string name in entries.Keys)
        {
            if (TryBlobAddress(name, out string? address))
            {
                present.Add(address);
            }
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                // Already a Corruption finding from CheckCount. The shape cannot be read, so the
                // cross-check has nothing to say rather than a second complaint about one defect.
                return;
            }

            var missing = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement version in document.RootElement.EnumerateArray())
            {
                if (version.ValueKind is not JsonValueKind.Object
                    || !version.TryGetProperty("blobAddress", out JsonElement address)
                    || address.ValueKind is not JsonValueKind.String)
                {
                    continue;
                }

                string? cited = address.GetString();
                if (!string.IsNullOrWhiteSpace(cited) && !present.Contains(cited))
                {
                    missing.Add(cited);
                }
            }

            foreach (string absent in missing.Order(StringComparer.Ordinal))
            {
                findings.Add(new SnapshotFinding(SnapshotFindingKind.MissingEntry, BlobEntryName(absent),
                    "A stored memory version cites this body, but the archive does not contain it."));
            }
        }
        catch (JsonException)
        {
            // Already a Corruption finding from CheckCount.
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

    /// <summary>
    /// Reads the archive's members, converting a container that cannot be read at all into a finding.
    /// </summary>
    /// <remarks>
    /// The exception's own message is deliberately not carried into the finding: the I/O ones embed
    /// the full archive path, and a finding is a value returned to a caller and rendered in a report,
    /// so a filesystem layout would become part of the diagnostic surface. The exception *type* is
    /// kept, since "truncated" (end of stream) and "not a tar" (invalid data) are different answers
    /// for the operator and neither is guessable from a fixed sentence.
    ///
    /// Cancellation is not caught: an aborted verify is not a defect in the archive.
    /// </remarks>
    private static (Dictionary<string, byte[]> Entries, IReadOnlyList<string> RepeatedNames, SnapshotFinding? Unreadable)
        TryReadEntries(string archivePath)
    {
        try
        {
            (var entries, var repeated) = ReadEntries(archivePath);
            return (entries, repeated, null);
        }
        catch (UnauthorizedAccessException)
        {
            // A permissions refusal proves nothing about the archive's content, so it must not read
            // as Corruption: restore turns Corruption into the integrity exit code, and an operator
            // would replace a sound archive instead of fixing a file mode.
            return (
                new Dictionary<string, byte[]>(StringComparer.Ordinal),
                [],
                new SnapshotFinding(
                    SnapshotFindingKind.Unreadable,
                    null,
                    "Archive could not be opened: access was denied. This is a permissions problem, not evidence about the archive's integrity."));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return (
                new Dictionary<string, byte[]>(StringComparer.Ordinal),
                [],
                new SnapshotFinding(
                    SnapshotFindingKind.Corruption,
                    null,
                    $"Archive could not be read as a tar container ({ex.GetType().Name}). It is truncated, not a tar archive, or unreadable."));
        }
    }

    private static (Dictionary<string, byte[]> Entries, IReadOnlyList<string> RepeatedNames) ReadEntries(string archivePath)
    {
        using FileStream stream = File.OpenRead(archivePath);
        using var tar = new TarReader(stream);

        // A repeated member name is tamper, not a duplicate to collapse: every member except the
        // manifest is hash-declared, so one name appearing twice means the archive carries a member
        // the manifest never described. Collect the repeats alongside the map and let VerifyAsync
        // report them; the readers below keep last-wins, which is the only ordering a corrupt
        // archive is read in anyway.
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var repeated = new List<string>();
        while (tar.GetNextEntry() is { } entry)
        {
            using var buffer = new MemoryStream();
            entry.DataStream?.CopyTo(buffer);
            if (!entries.TryAdd(entry.Name, buffer.ToArray()))
            {
                repeated.Add(entry.Name);
            }
        }

        return (entries, repeated);
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
