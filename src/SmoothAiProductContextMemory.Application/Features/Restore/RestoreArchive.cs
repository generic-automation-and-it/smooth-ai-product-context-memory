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
        RestoreResults Restored);

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
                    $"Archive is missing blob entries for {missing.Length} referenced address(es).");
            }

            RestoreResults restored = await repository.RestoreAsync(
                request.ConnectionString,
                capture,
                request.OverrideNonEmpty,
                cancellationToken);

            await RestoreObjectsAsync(opened, addresses, cancellationToken);

            var lines = new List<ReconciliationLine>
            {
                new($"row memories  manifest={opened.Manifest.Counts.Memories} restored={restored.Memories}", restored.Memories == opened.Manifest.Counts.Memories),
                new($"row versions  manifest={opened.Manifest.Counts.Versions} restored={restored.Versions}", restored.Versions == opened.Manifest.Counts.Versions),
                new($"graph vertices manifest={opened.Manifest.Counts.Vertices} restored={restored.Vertices}", restored.Vertices == opened.Manifest.Counts.Vertices),
                new($"graph edges    manifest={opened.Manifest.Counts.Edges} restored={restored.Edges}", restored.Edges == opened.Manifest.Counts.Edges),
                new($"objects        manifest={opened.Manifest.Counts.Objects} restored={restored.Objects}", restored.Objects == opened.Manifest.Counts.Objects),
                new($"ticket vertices manifest={opened.Manifest.Counts.TicketVertices} restored={restored.TicketVertices}", restored.TicketVertices == opened.Manifest.Counts.TicketVertices),
                new($"ticket edges   manifest={opened.Manifest.Counts.TicketEdges} restored={restored.TicketEdges}", restored.TicketEdges == opened.Manifest.Counts.TicketEdges),
                new($"traversal      restored paths={restored.TraversalPathCount} manifest edges={opened.Manifest.Counts.Edges}", restored.TraversalPathCount == opened.Manifest.Counts.Edges),
            };

            bool reconciled = lines.All(l => l.Matches);

            logger.LogInformation(
                "Restore reconciliation completed. Reconciled: {Reconciled} TraversalPaths: {TraversalPaths}",
                reconciled,
                restored.TraversalPathCount);

            return new Response(reconciled, lines, restored);
        }

        private static string[] ReferencedAddresses(SnapshotCapture capture) =>
            capture.MemoryVersions
                .Select(v => v.BlobAddress)
                .Where(static a => !string.IsNullOrEmpty(a))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();

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
