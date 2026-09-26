using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

namespace SmoothAiProductContextMemory.Application.Features.Snapshot;

/// <summary>
/// One portable, self-verifying corpus snapshot: capture both stores from one consistent moment,
/// walk every database-cited blob reference, and write a single archive with a self-verifying
/// manifest. Read-only against both stores; nothing is deleted (LADR-03 / LADR-06 / HLD-001 NFR-05).
/// </summary>
public static class SnapshotStore
{
    public sealed record Request(string ConnectionString, string DestinationPath) : IRequest<Response>;

    public sealed record Response(
        string DestinationPath,
        int Memories,
        int Versions,
        int Vertices,
        int Edges,
        int Objects,
        int TicketVertices,
        int TicketEdges,
        int DanglingReferences,
        int UnreferencedObjects,
        int MismatchedBodies);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.ConnectionString).NotEmpty().MaximumLength(2048);
            RuleFor(x => x.DestinationPath).NotEmpty().MaximumLength(1024);
        }
    }

    public sealed class Handler(
        ISnapshotRepository repository,
        ISnapshotArchive archive,
        IBlobStorage blobStorage,
        ISnapshotMetadataStore metadataStore,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Corpus snapshot started");

            SnapshotCaptureResult captured = await repository.CaptureAsync(
                request.ConnectionString,
                cancellationToken);

            logger.LogInformation(
                "Corpus snapshot captured. Memories: {Memories} Versions: {Versions} Vertices: {Vertices} Edges: {Edges}",
                captured.Counts.Memories,
                captured.Counts.Versions,
                captured.Counts.Vertices,
                captured.Counts.Edges);

            SnapshotWriteReport written = await archive.WriteAsync(
                request.DestinationPath,
                captured.Capture,
                captured.Walk,
                ReadBlobAsync,
                cancellationToken);

            logger.LogInformation(
                "Corpus snapshot completed. Destination: {Destination} Dangling: {Dangling} Orphans: {Orphans} Mismatched: {Mismatched}",
                written.DestinationPath,
                written.DanglingReferences,
                written.UnreferencedObjects,
                written.MismatchedBodies);

            await metadataStore.WriteAsync(
                new SnapshotMetadata(
                    DateTimeOffset.UtcNow,
                    written.Counts,
                    written.DanglingReferences,
                    written.UnreferencedObjects),
                cancellationToken);

            return new Response(
                written.DestinationPath,
                written.Counts.Memories,
                written.Counts.Versions,
                written.Counts.Vertices,
                written.Counts.Edges,
                written.Counts.Objects,
                written.Counts.TicketVertices,
                written.Counts.TicketEdges,
                written.DanglingReferences,
                written.UnreferencedObjects,
                written.MismatchedBodies);
        }

        private async Task<byte[]> ReadBlobAsync(string address)
        {
            BlobContent? content = await blobStorage.GetAsync(address);
            if (content is null)
            {
                throw new InvalidOperationException($"Blob referenced by the captured state is missing: {address}");
            }

            await using (content)
            {
                using var buffer = new MemoryStream();
                await content.Content.CopyToAsync(buffer);
                return buffer.ToArray();
            }
        }
    }
}
