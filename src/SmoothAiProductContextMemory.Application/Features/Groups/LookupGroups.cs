using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Application.Common.Retrieval;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Groups;

public static class LookupGroups
{
    public sealed record Request(
        IReadOnlyList<TicketInput>? Tickets = null,
        string? Repo = null,
        string? ScopeDimension = null,
        string? ScopeIdentifier = null) : IRequest<Response>;

    public sealed record Response(IReadOnlyList<ResolveGroup.Response> Items);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleForEach(x => x.Tickets).SetValidator(new TicketInputValidator());
            RuleFor(x => x.Tickets).Must(t => t is null || t.Count <= 100);
            RuleFor(x => x.Repo).MaximumLength(500);
            RuleFor(x => x.ScopeDimension).MaximumLength(32);
            RuleFor(x => x.ScopeIdentifier).MaximumLength(500);
            RuleFor(x => x).Must(x => x.Tickets is { Count: > 0 } || x.Repo is not null || x.ScopeDimension is not null)
                .WithMessage("Supply a ticket, repository, or scope selector.");
        }
    }

    public sealed class Handler(IApplicationDbContext db) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            IQueryable<MemoryGroup> query = db.MemoryGroups.AsNoTracking();
            if (request.Tickets is { Count: > 0 })
            {
                var owners = new HashSet<long>();
                foreach (TicketInput ticket in request.Tickets)
                {
                    MemoryGroup? owner = await TicketLookup.FindGroupByTicketAsync(db, ticket.Provider, ticket.Key, cancellationToken);
                    if (owner is not null)
                    {
                        owners.Add(owner.Id);
                    }
                }

                query = query.Where(g => owners.Contains(g.Id));
            }

            if (request.Repo is { } repo) { query = query.Where(g => g.Repo == repo); }
            if (request.ScopeDimension is { } dimension) { query = query.Where(g => g.ScopeDimension == dimension); }
            if (request.ScopeIdentifier is { } identifier) { query = query.Where(g => g.ScopeIdentifier == identifier); }

            string[] hiddenDimensions = MemoryScopeFilter.Plan(request.ScopeDimension, request.Tickets is { Count: > 0 })
                .ExcludedDimensions.ToArray();
            query = query.Where(g => !hiddenDimensions.Contains(g.ScopeDimension));
            MemoryGroup[] groups = await query.OrderBy(g => g.Uuid).Take(100).ToArrayAsync(cancellationToken);
            long[] initiativeIds = groups.Select(g => g.InitiativeId).Distinct().ToArray();
            Dictionary<long, string> initiatives = await db.Initiatives.AsNoTracking()
                .Where(i => initiativeIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name, cancellationToken);
            return new Response(groups.Select(g => new ResolveGroup.Response(g.Uuid, false, g.ScopeDimension,
                g.ScopeIdentifier, g.Repo, initiatives[g.InitiativeId],
                g.Tickets.Select(t => new TicketInput(t.Provider, t.Key, t.Url)).ToArray())).ToArray());
        }
    }
}
