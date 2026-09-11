using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Application.Common.Retrieval;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Memories;

public static class QueryMemories
{
    public sealed record Request(
        string? Query,
        IReadOnlyList<string>? Facets,
        IReadOnlyList<string>? Tags,
        string? Kind,
        string? Status,
        string? ScopeDimension,
        Guid? GroupUuid,
        string? TicketProvider,
        string? TicketKey,
        string? Repo,
        string? InitiativeName,
        bool IncludeProposed = false,
        bool CurrentOnly = true) : IRequest<Response>;

    public sealed record Response(IReadOnlyList<CheapMemory> Items);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Kind).MaximumLength(64);
            RuleFor(x => x.Status).MaximumLength(32);
            RuleFor(x => x.ScopeDimension).MaximumLength(32);
        }
    }

    public sealed class Handler(IApplicationDbContext db, ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Query memories started");

            bool hasGroupContext = request.GroupUuid is not null
                || (!string.IsNullOrWhiteSpace(request.TicketProvider) && !string.IsNullOrWhiteSpace(request.TicketKey));

            IQueryable<Memory> memories = db.Memories
                .AsNoTracking()
                .Include(m => m.Group)
                .Include(m => m.Versions);

            if (request.GroupUuid is { } groupUuid)
            {
                memories = memories.Where(m => m.Group != null && m.Group.Uuid == groupUuid);
            }

            if (!string.IsNullOrWhiteSpace(request.Repo))
            {
                memories = memories.Where(m => m.Group != null && m.Group.Repo == request.Repo);
            }

            if (!string.IsNullOrWhiteSpace(request.InitiativeName))
            {
                memories = memories.Where(m =>
                    m.Group != null
                    && db.Initiatives.Any(i => i.Id == m.Group.InitiativeId && i.Name == request.InitiativeName));
            }

            if (request.Facets is { Count: > 0 })
            {
                foreach (string facet in request.Facets)
                {
                    memories = memories.Where(m => m.Facets.Contains(facet));
                }
            }

            if (request.Tags is { Count: > 0 })
            {
                foreach (string tag in request.Tags)
                {
                    memories = memories.Where(m => m.Tags.Contains(tag));
                }
            }

            if (!string.IsNullOrWhiteSpace(request.Query))
            {
                string q = request.Query;
                memories = memories.Where(m =>
                    m.Name.Contains(q)
                    || m.Description.Contains(q)
                    || m.Versions.Any(v => v.Statement.Contains(q) || v.ContentSummary.Contains(q)));
            }

            if (!string.IsNullOrWhiteSpace(request.TicketProvider) && !string.IsNullOrWhiteSpace(request.TicketKey))
            {
                MemoryGroup? ticketGroup = await TicketLookup.FindGroupByTicketAsync(
                    db, request.TicketProvider, request.TicketKey, cancellationToken);
                if (ticketGroup is null)
                {
                    logger.LogInformation("Query memories completed. Count: {Count}", 0);
                    return new Response([]);
                }

                memories = memories.Where(m => m.GroupId == ticketGroup.Id);
            }

            List<Memory> loaded = await memories.ToListAsync(cancellationToken);

            IEnumerable<Memory> scoped = loaded.Where(m =>
                m.Group is not null
                && MemoryScopeFilter.IncludeGroup(m.Group.ScopeDimension, request.ScopeDimension, hasGroupContext));

            var items = new List<CheapMemory>();
            foreach (Memory memory in scoped)
            {
                IEnumerable<MemoryVersion> versions = request.CurrentOnly
                    ? memory.Versions.Where(v => v.IsCurrent)
                    : memory.Versions;

                foreach (MemoryVersion version in versions)
                {
                    if (!string.IsNullOrWhiteSpace(request.Kind) && version.Kind != request.Kind)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(request.Status))
                    {
                        if (version.Status != request.Status)
                        {
                            continue;
                        }
                    }
                    else if (!request.IncludeProposed
                             && version.Status == MemoryVersion.MemoryVersionStatus.Proposed)
                    {
                        continue;
                    }

                    items.Add(ToCheap(memory, version));
                }
            }

            logger.LogInformation("Query memories completed. Count: {Count}", items.Count);
            return new Response(items);
        }

        private static CheapMemory ToCheap(Memory memory, MemoryVersion version) =>
            new(
                memory.Uuid,
                memory.Group!.Uuid,
                memory.Name,
                memory.Description,
                version.Statement,
                version.ContentSummary,
                version.Kind,
                memory.Facets,
                memory.Tags,
                version.Status,
                version.Confidence,
                memory.Group.ScopeDimension,
                memory.Group.ScopeIdentifier,
                version.ValidFrom,
                version.ValidUntil,
                version.Version,
                version.IsCurrent);
    }
}
