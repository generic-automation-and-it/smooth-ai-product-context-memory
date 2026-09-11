using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Memories;

public static class GetMemoryBlob
{
    public sealed record Request(Guid Uuid, int Version) : IRequest<Response>;

    public sealed record Response(Stream Content, string ContentType);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Uuid).NotEmpty();
            RuleFor(x => x.Version).GreaterThan(0);
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
