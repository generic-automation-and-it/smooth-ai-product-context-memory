using Mediator;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

namespace SmoothAiProductContextMemory.Application.Features.Snapshot;

/// <summary>
/// Read-only operational self-check: the age of the most recent snapshot (never a scheduled backup),
/// the corpus counts from that snapshot, and the last walk's orphan/dangling numbers (LADR-04). It
/// serves the last snapshot's stored result rather than re-running an expensive walk per request, so
/// staleness is a stated fact the practitioner encounters, never a chore they must remember (BR-01).
/// </summary>
public static class SnapshotPreflight
{
    public sealed record Request() : IRequest<Response>;

    public sealed record Response(
        DateTimeOffset? LastSnapshotAt,
        string Recency,
        int Memories,
        int Versions,
        int Vertices,
        int Edges,
        int Objects,
        int DanglingReferences,
        int UnreferencedObjects);

    public sealed class Handler(
        ISnapshotMetadataStore metadataStore,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Snapshot preflight check started");

            SnapshotMetadata? metadata = await metadataStore.ReadAsync(cancellationToken);

            logger.LogInformation(
                "Snapshot preflight check completed. HasSnapshot: {HasSnapshot}",
                metadata is not null);

            if (metadata is null)
            {
                return new Response(null, "never", 0, 0, 0, 0, 0, 0, 0);
            }

            TimeSpan age = DateTimeOffset.UtcNow - metadata.LastSnapshotAt;

            return new Response(
                metadata.LastSnapshotAt,
                FormatRecency(age),
                metadata.Counts.Memories,
                metadata.Counts.Versions,
                metadata.Counts.Vertices,
                metadata.Counts.Edges,
                metadata.Counts.Objects,
                metadata.DanglingReferences,
                metadata.UnreferencedObjects);
        }

        // Scale to minutes/hours/days so the recency statement stays readable as the snapshot ages
        // (raw minutes reads "10080 minutes ago" at a week). Clamp at zero: clock skew ahead of the
        // capture time would otherwise render a negative age.
        private static string FormatRecency(TimeSpan age)
        {
            if (age < TimeSpan.Zero) return "just now";
            if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} minutes ago";
            if (age.TotalDays < 1) return $"{(int)age.TotalHours} hours ago";
            return $"{(int)age.TotalDays} days ago";
        }
    }
}
