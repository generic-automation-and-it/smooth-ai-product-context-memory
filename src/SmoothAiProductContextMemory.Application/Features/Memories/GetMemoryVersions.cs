using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Application.Common.Retrieval;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Memories;

public static class GetMemoryVersions
{
    public sealed record Request(Guid Uuid, string? ScopeDimension = null) : IRequest<Response>;

    public sealed record Response(IReadOnlyList<CheapMemory> Items);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Uuid).NotEmpty();
        }
    }

    public sealed class Handler(IApplicationDbContext db, ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Get memory versions started");

            Memory memory = await db.Memories
                .AsNoTracking()
                .Include(m => m.Group)
                .Include(m => m.Versions)
                .SingleOrDefaultAsync(m => m.Uuid == request.Uuid, cancellationToken)
                ?? throw new NotFoundException($"Memory '{request.Uuid}' was not found.");

            string dimension = memory.Group!.ScopeDimension;
            if (!MemoryScopeFilter.IncludeGroup(dimension, request.ScopeDimension, hasGroupContext: false))
            {
                logger.LogDebug("Memory version history blocked by scope. Dimension: {Dimension}", dimension);
                throw new ForbiddenException(
                    $"This memory is '{dimension}'-scoped. Request it with scope '{dimension}' to read it.");
            }

            List<CheapMemory> items =
            [
                .. memory.Versions
                    .OrderBy(v => v.Version)
                    .Select(v => new CheapMemory(
                        memory.Uuid,
                        memory.Group!.Uuid,
                        memory.Name,
                        memory.Description,
                        v.Statement,
                        v.ContentSummary,
                        v.Kind,
                        memory.Facets,
                        memory.Tags,
                        v.Status,
                        v.Confidence,
                        memory.Group.ScopeDimension,
                        memory.Group.ScopeIdentifier,
                        v.ValidFrom,
                        v.ValidUntil,
                        v.Version,
                        v.IsCurrent))
            ];

            logger.LogInformation("Get memory versions completed. Count: {Count}", items.Count);
            return new Response(items);
        }
    }
}
