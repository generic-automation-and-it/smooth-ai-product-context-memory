using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

namespace SmoothAiProductContextMemory.Application.Features.Verify;

/// <summary>
/// Offline verification of a corpus snapshot archive. Consumes only the archive — no service, no
/// database, no object store, no network. Detects any truncated, altered or missing entry; a blob
/// whose hash disagrees with its own archive hash is corruption, while a blob whose content hash
/// disagrees with the database-cited address is reported as capture-time cross-store inconsistency.
/// </summary>
public static class VerifyArchive
{
    public sealed record Request(string ArchivePath) : IRequest<Response>;

    public sealed record Response(bool IsClean, IReadOnlyList<SnapshotFinding> Findings);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.ArchivePath).NotEmpty().MaximumLength(1024);
        }
    }

    public sealed class Handler(
        ISnapshotArchive archive,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Archive verification started");

            SnapshotVerification verification = await archive.VerifyAsync(
                request.ArchivePath,
                cancellationToken);

            logger.LogInformation(
                "Archive verification completed. Clean: {IsClean} Findings: {FindingCount}",
                verification.IsClean,
                verification.Findings.Count);

            return new Response(verification.IsClean, verification.Findings);
        }
    }
}
