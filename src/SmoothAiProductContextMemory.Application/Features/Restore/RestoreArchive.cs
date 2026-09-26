using System.Security.Cryptography;
using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Restore;

/// <summary>
/// Restores both stores from a snapshot archive into an empty target, after recalling the
/// archive's captured state and its blob bodies, then prints a reconciliation against the manifest
/// so the operator sees the arithmetic close (LADR-05). Refuses a non-empty target unless
/// explicitly overridden; a partial restore never reports success (NFR-02).
/// </summary>
public static class RestoreArchive
{
    public sealed record Request(string ArchivePath, string ConnectionString, bool OverrideNonEmpty = false)
        : IRequest<Response>;

    public sealed record Response(
        bool Reconciled,
        IReadOnlyList<ReconciliationLine> Lines,
        RestoreResults Restored,
        int Objects);

    public sealed record ReconciliationLine(string Statement, bool Matches);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.ArchivePath).NotEmpty().MaximumLength(1024);
            RuleFor(x => x.ConnectionString).NotEmpty().MaximumLength(2048);
        }
    }

    public sealed class Handler(
        ISnapshotRepository repository,
        ISnapshotArchive archive,
        IBlobStorage blobStorage,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Restore started");

            // Verifying the archive is a prerequisite to any mutation, so a tampered archive (or one
            // the verifier already rejects) is refused before either store is touched (R01).
            SnapshotVerification verification = await archive.VerifyAsync(request.ArchivePath, cancellationToken);
            if (!verification.IsClean)
            {
                throw new InvalidOperationException(
                    $"Archive failed verification with {verification.Findings.Count} finding(s); restore refused so no mutation is attempted.");
            }

            SnapshotCapture capture = await archive.ReadCaptureAsync(request.ArchivePath, cancellationToken);
            SnapshotArchive opened = await archive.ReadAsync(request.ArchivePath, cancellationToken);

            // Pre-validate every referenced blob body is present before any DB mutation, so a missing
            // entry fails the restore loudly with no partial success and no --force-on-retry (NFR-02).
            // ReadBlob would throw IndexOutOfRange on an absent entry, so check membership first.
            string[] addresses = ReferencedAddresses(capture);
            string[] missing = addresses.Where(a => !opened.ContainsBlob(a)).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Archive is missing {missing.Length} referenced blob entr(ies); restore is refused so the database is never left citing absent bodies. This is either a truncated/tampered archive or a dangling reference that was already unresolvable at capture (which verify reports as clean but cannot restore).");
            }

            // Refuse a non-empty target before writing anything to either store; the repository
            // re-checks inside its transaction.
            if (!request.OverrideNonEmpty
                && !await repository.IsTargetEmptyAsync(request.ConnectionString, cancellationToken))
            {
                throw new InvalidOperationException(
                    "Restore target is not empty; overrideNonEmpty is required to clear it.");
            }

            // Bodies go first: content-addressed writes are idempotent, so a later database failure
            // leaves at worst unreferenced objects, never a committed database citing absent bodies.
            await RestoreObjectsAsync(opened, addresses, cancellationToken);
            int objects = await VerifyRestoredObjectsAsync(addresses, cancellationToken);
            SnapshotCounts expected = opened.Manifest.Counts;
            if (objects != expected.Objects)
            {
                throw new InvalidOperationException(
                    $"Object store holds {objects} of {expected.Objects} referenced bodies after restore; database left untouched.");
            }

            RestoreResults restored = await repository.RestoreAsync(
                request.ConnectionString,
                capture,
                expected,
                request.OverrideNonEmpty,
                cancellationToken);

            var lines = new List<ReconciliationLine>
            {
                new($"row memories   manifest={expected.Memories} restored={restored.Memories}", restored.Memories == expected.Memories),
                new($"row versions   manifest={expected.Versions} restored={restored.Versions}", restored.Versions == expected.Versions),
                new($"graph vertices manifest={expected.Vertices} restored={restored.Vertices}", restored.Vertices == expected.Vertices),
                new($"graph edges    manifest={expected.Edges} restored={restored.Edges}", restored.Edges == expected.Edges),
                new($"objects        manifest={expected.Objects} restored={objects}", objects == expected.Objects),
                new($"ticket vertices manifest={expected.TicketVertices} restored={restored.TicketVertices}", restored.TicketVertices == expected.TicketVertices),
                new($"ticket edges   manifest={expected.TicketEdges} restored={restored.TicketEdges}", restored.TicketEdges == expected.TicketEdges),
                new($"traversal      restored paths={restored.TraversalPathCount} manifest edges={expected.Edges}", restored.TraversalPathCount == expected.Edges),
                new($"committed      {(restored.Committed ? "yes" : "no — rolled back")}", restored.Committed),
            };

            bool reconciled = lines.All(l => l.Matches);

            logger.LogInformation(
                "Restore reconciliation completed. Reconciled: {Reconciled} TraversalPaths: {TraversalPaths}",
                reconciled,
                restored.TraversalPathCount);

            return new Response(reconciled, lines, restored, objects);
        }

        private static string[] ReferencedAddresses(SnapshotCapture capture) =>
            capture.MemoryVersions
                .Select(v => v.BlobAddress)
                .Where(static a => !string.IsNullOrEmpty(a))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        private async Task<int> VerifyRestoredObjectsAsync(
            IReadOnlyList<string> addresses,
            CancellationToken cancellationToken)
        {
            // Restore must prove each destination body hashes to its address and refuse a corrupted
            // or mismatched pre-existing object, not merely confirm a key exists (R03). The S3 writer
            // skips upload when the destination key already exists, so existence is not integrity.
            int present = 0;
            foreach (string address in addresses)
            {
                BlobContent? blob = await blobStorage.GetAsync(address, cancellationToken);
                if (blob is null)
                {
                    throw new InvalidOperationException(
                        $"Restored body '{address}' is missing from the object store after restore; database left untouched.");
                }

                await using (blob)
                {
                    if (!ContentMatchesAddress(await ReadAllAsync(blob.Content, cancellationToken), address))
                    {
                        throw new InvalidOperationException(
                            $"Restored body '{address}' does not hash to its content address; refusing to report success while the destination is corrupt.");
                    }
                }

                present++;
            }

            return present;
        }

        private static bool ContentMatchesAddress(byte[] content, string address)
        {
            const char separator = '/';
            string expected = address[(address.LastIndexOf(separator) + 1)..];
            string actual = Convert.ToHexStringLower(SHA256.HashData(content));
            return string.Equals(actual, expected, StringComparison.Ordinal);
        }

        private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
        {
            await using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }

        private async Task RestoreObjectsAsync(
            SnapshotArchive opened,
            IReadOnlyList<string> addresses,
            CancellationToken cancellationToken)
        {
            foreach (string address in addresses)
            {
                byte[] body = opened.ReadBlob(address);
                await blobStorage.StoreAsync(new MemoryStream(body, writable: false), cancellationToken: cancellationToken);
            }
        }
    }
}
