using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Application.Common.Retrieval;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Memories;

/// <summary>
/// The cheap fields of one memory's current version, read by identity.
/// </summary>
/// <remarks>
/// This is the read a graph hop needs: a traversal returns <c>uuid</c> references for every hop except
/// the endpoint, so labelling an intermediate node requires a by-identity read that the query endpoint
/// cannot express (it has no uuid predicate). It stays a *cheap* read — no blob, no version chain, and
/// the same scope rule the blob and version-history reads carry, because holding a uuid is not authority
/// to read a dimension the scope rule hides.
/// </remarks>
public static class GetMemory
{
    public sealed record Request(Guid Uuid, string? ScopeDimension = null) : IRequest<Response>;

    public sealed record Response(CheapMemory Item);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Uuid).NotEmpty();
            RuleFor(x => x.ScopeDimension).MaximumLength(32);
        }
    }

    public sealed class Handler(IApplicationDbContext db, IMemoryGraph graph, ILogger<Handler> logger)
        : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Get memory started");

            MemoryVersion? current = await db.MemoryVersions
                .AsNoTracking()
                .Include(v => v.Memory)
                    .ThenInclude(m => m!.Group)
                .SingleOrDefaultAsync(
                    v => v.Memory != null && v.Memory.Uuid == request.Uuid && v.IsCurrent,
                    cancellationToken);

            if (current?.Memory?.Group is null)
            {
                throw new NotFoundException($"Memory '{request.Uuid}' was not found.");
            }

            Memory memory = current.Memory;
            string dimension = memory.Group.ScopeDimension;
            if (!MemoryScopeFilter.IncludeGroup(dimension, request.ScopeDimension, hasGroupContext: false))
            {
                logger.LogDebug("Memory read blocked by scope. Dimension: {Dimension}", dimension);
                throw new ForbiddenException(
                    $"This memory is '{dimension}'-scoped. Request it with scope '{dimension}' to read it.");
            }

            MemoryGroup group = memory.Group;
            string? initiativeName = await db.Initiatives
                .AsNoTracking()
                .Where(i => i.Id == group.InitiativeId)
                .Select(i => i.Name)
                .SingleOrDefaultAsync(cancellationToken);

            CheapMemory bare = new(
                memory.Uuid,
                memory.Group.Uuid,
                memory.Name,
                memory.Description,
                current.Statement,
                current.ContentSummary,
                current.Kind,
                memory.Facets,
                memory.Tags,
                current.Status,
                current.Confidence,
                memory.Group.ScopeDimension,
                memory.Group.ScopeIdentifier,
                current.ValidFrom,
                current.ValidUntil,
                current.Version,
                current.IsCurrent,
                current.Sources,
                current.CreatedOn)
            {
                Repository = group.Repo,
                InitiativeName = initiativeName,
            };

            // Same assembly as the query and version-history paths, so a hop label and a
            // list row never disagree about a memory's binding (tickets live in two stores).
            IReadOnlyList<CheapMemory> enriched =
                await TicketIdentityLookup.EnrichAsync(db, graph, [bare], cancellationToken);

            logger.LogInformation("Get memory completed. Version: {Version}", current.Version);
            return new Response(enriched[0]);
        }
    }
}
