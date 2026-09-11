using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Application.Common.Retrieval;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Memories;

/// <summary>
/// Blob drill-down, proxied so the store's own URLs never reach the caller.
/// </summary>
/// <remarks>
/// The proxy also carries the scope rule. Holding a uuid is not authority to read programme
/// knowledge as product fact, so the same plan that hides a dimension from an open query blocks the
/// drill-down unless the caller names that scope explicitly. Without this the proxy would be a
/// bypass rather than a boundary.
/// </remarks>
public static class GetMemoryBlob
{
    public sealed record Request(Guid Uuid, int Version, string? ScopeDimension = null) : IRequest<Response>;

    public sealed record Response(Stream Content, string ContentType);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Uuid).NotEmpty();
            RuleFor(x => x.Version).GreaterThan(0);
            RuleFor(x => x.ScopeDimension).MaximumLength(32);
        }
    }

    public sealed class Handler(
        IApplicationDbContext db,
        IBlobStorage blobStorage,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Get memory blob started");

            MemoryVersion? version = await db.MemoryVersions
                .AsNoTracking()
                .Include(v => v.Memory)
                    .ThenInclude(m => m!.Group)
                .SingleOrDefaultAsync(
                    v => v.Memory != null && v.Memory.Uuid == request.Uuid && v.Version == request.Version,
                    cancellationToken);

            if (version?.BlobAddress is null)
            {
                throw new NotFoundException($"Blob for memory '{request.Uuid}' version {request.Version} was not found.");
            }

            string dimension = version.Memory!.Group!.ScopeDimension;
            if (!MemoryScopeFilter.IncludeGroup(dimension, request.ScopeDimension, hasGroupContext: false))
            {
                logger.LogDebug("Blob drill-down blocked by scope. Dimension: {Dimension}", dimension);
                throw new ForbiddenException(
                    $"This memory is '{dimension}'-scoped. Request it with scope '{dimension}' to read it.");
            }

            BlobContent? blob = await blobStorage.GetAsync(version.BlobAddress, cancellationToken);
            if (blob is null)
            {
                throw new NotFoundException($"Blob for memory '{request.Uuid}' version {request.Version} was not found.");
            }

            logger.LogInformation("Get memory blob completed");
            return new Response(blob.Content, blob.ContentType ?? "application/octet-stream");
        }
    }
}
