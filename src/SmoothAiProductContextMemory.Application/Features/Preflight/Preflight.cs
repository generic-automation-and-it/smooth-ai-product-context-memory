using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Preflight;

/// <summary>
/// Exact-match recall that makes deduplication, contradiction detection and ticket uniqueness
/// enforceable. Writes nothing and judges nothing — the caller decides new / version / skip.
/// </summary>
/// <remarks>
/// Subject lookup is deliberately not scoped to a group: the same subject asserted in another group
/// is exactly what the caller needs to see.
/// </remarks>
public static class Preflight
{
    public const int MaxCandidates = 20;

    public sealed record Candidate(
        string Description,
        string? Kind,
        IReadOnlyList<string>? Facets,
        TicketInput? Ticket);

    public sealed record Match(
        Guid Uuid,
        Guid GroupUuid,
        string Description,
        string SubjectSlug,
        string Kind,
        IReadOnlyList<string> Facets);

    public sealed record TicketConflict(string Provider, string Key, Guid GroupUuid);

    public sealed record IntraBatchCollision(int LeftIndex, int RightIndex, string SubjectSlug);

    public sealed record CandidateResult(
        int Index,
        IReadOnlyList<Match> Matches,
        TicketConflict? TicketConflict);

    public sealed record Request(IReadOnlyList<Candidate> Candidates) : IRequest<Response>;

    public sealed record Response(
        IReadOnlyList<CandidateResult> Candidates,
        IReadOnlyList<IntraBatchCollision> IntraBatchCollisions);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Candidates).NotNull().NotEmpty();

            // Guarded rather than RuleFor(x => x.Candidates.Count): every rule is evaluated, so
            // dereferencing the list here would throw on a null body instead of returning 400.
            RuleFor(x => x.Candidates)
                .Must(c => c is null || c.Count <= MaxCandidates)
                .WithMessage($"At most {MaxCandidates} candidates per request.");
            RuleForEach(x => x.Candidates).ChildRules(c =>
            {
                c.RuleFor(x => x.Description).NotEmpty();
            });
        }
    }

    public sealed class Handler(IApplicationDbContext db, ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Preflight started. Count: {Count}", request.Candidates.Count);

            var collisions = new List<IntraBatchCollision>();
            var slugs = request.Candidates
                .Select((c, i) => (Index: i, Slug: Slug.Subject(c.Description)))
                .ToList();

            for (int i = 0; i < slugs.Count; i++)
            {
                for (int j = i + 1; j < slugs.Count; j++)
                {
                    if (slugs[i].Slug == slugs[j].Slug)
                    {
                        collisions.Add(new IntraBatchCollision(slugs[i].Index, slugs[j].Index, slugs[i].Slug));
                    }
                }
            }

            var results = new List<CandidateResult>(request.Candidates.Count);
            for (int i = 0; i < request.Candidates.Count; i++)
            {
                Candidate candidate = request.Candidates[i];
                string slug = slugs[i].Slug;

                IQueryable<Memory> query = db.Memories
                    .AsNoTracking()
                    .Include(m => m.Group)
                    .Include(m => m.Versions.Where(v => v.IsCurrent))
                    .Where(m => m.SubjectSlug == slug);

                if (candidate.Facets is { Count: > 0 })
                {
                    foreach (string facet in candidate.Facets)
                    {
                        query = query.Where(m => m.Facets.Contains(facet));
                    }
                }

                // Kind narrows before the cap, not after: filtering a truncated page would report
                // "no match" for a subject that does match on a row the cap dropped.
                if (!string.IsNullOrWhiteSpace(candidate.Kind))
                {
                    string kind = candidate.Kind;
                    query = query.Where(m => m.Versions.Any(v => v.IsCurrent && v.Kind == kind));
                }

                List<Memory> matches = await query
                    .OrderBy(m => m.Id)
                    .Take(MaxCandidates)
                    .ToListAsync(cancellationToken);

                TicketConflict? ticketConflict = null;
                if (candidate.Ticket is { } ticket)
                {
                    MemoryGroup? owner = await TicketLookup.FindGroupByTicketAsync(
                        db, ticket.Provider, ticket.Key, cancellationToken);
                    if (owner is not null)
                    {
                        ticketConflict = new TicketConflict(ticket.Provider, ticket.Key, owner.Uuid);
                    }
                }

                results.Add(new CandidateResult(
                    i,
                    [
                        .. matches.Select(m => new Match(
                            m.Uuid,
                            m.Group!.Uuid,
                            m.Description,
                            m.SubjectSlug,
                            m.Versions.FirstOrDefault()?.Kind ?? string.Empty,
                            m.Facets))
                    ],
                    ticketConflict));
            }

            logger.LogInformation("Preflight completed. Count: {Count}", results.Count);
            return new Response(results, collisions);
        }
    }
}
