using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Application.Common.Retrieval;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Memories;

/// <summary>
/// Hybrid retrieval over the cheap fields. Blob bodies are never returned here.
/// </summary>
/// <remarks>
/// The handler resolves identity and the scope rule, then hands a fully resolved
/// <see cref="MemorySearchCriteria"/> to <see cref="IMemorySearch"/>. Every predicate is executed by
/// the database: filtering in memory after materialising the rows would defeat the full-text, array
/// and validity indexes and would pull whole version chains across the wire on a current-only query.
/// </remarks>
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
        bool CurrentOnly = true,
        DateTimeOffset? AsOf = null,
        int Limit = MemorySearchDefaults.Limit) : IRequest<Response>;

    public sealed record Response(IReadOnlyList<CheapMemory> Items);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Kind).MaximumLength(64);
            RuleFor(x => x.Status).MaximumLength(32);
            RuleFor(x => x.ScopeDimension).MaximumLength(32);
            RuleFor(x => x.Limit).InclusiveBetween(1, MemorySearchDefaults.MaxLimit);
            RuleFor(x => x.TicketKey)
                .NotEmpty()
                .When(x => !string.IsNullOrWhiteSpace(x.TicketProvider))
                .WithMessage("TicketKey is required when TicketProvider is supplied.");
            RuleFor(x => x.TicketProvider)
                .NotEmpty()
                .When(x => !string.IsNullOrWhiteSpace(x.TicketKey))
                .WithMessage("TicketProvider is required when TicketKey is supplied.");
        }
    }

    public sealed class Handler(
        IApplicationDbContext db,
        IMemorySearch search,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Query memories started");

            bool hasTicket = !string.IsNullOrWhiteSpace(request.TicketProvider)
                && !string.IsNullOrWhiteSpace(request.TicketKey);
            bool hasGroupContext = request.GroupUuid is not null || hasTicket;

            long? groupId = null;
            if (hasTicket)
            {
                MemoryGroup? ticketGroup = await TicketLookup.FindGroupByTicketAsync(
                    db, request.TicketProvider!, request.TicketKey!, cancellationToken);
                if (ticketGroup is null)
                {
                    // A miss is a signal the caller acts on, not an error.
                    logger.LogInformation("Query memories completed. Count: {Count}", 0);
                    return new Response([]);
                }

                groupId = ticketGroup.Id;
            }

            MemoryScopeFilter.ScopeFilterPlan scope =
                MemoryScopeFilter.Plan(request.ScopeDimension, hasGroupContext);

            var criteria = new MemorySearchCriteria
            {
                FreeText = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query,
                Facets = request.Facets ?? [],
                Tags = request.Tags ?? [],
                Kind = string.IsNullOrWhiteSpace(request.Kind) ? null : request.Kind,
                Status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status,
                ExcludeProposed = !request.IncludeProposed,
                RequiredScopeDimension = scope.RequiredDimension,
                ExcludedScopeDimensions = scope.ExcludedDimensions,
                GroupUuid = request.GroupUuid,
                GroupId = groupId,
                Repo = string.IsNullOrWhiteSpace(request.Repo) ? null : request.Repo,
                InitiativeName = string.IsNullOrWhiteSpace(request.InitiativeName) ? null : request.InitiativeName,
                AsOf = request.AsOf,
                CurrentOnly = request.CurrentOnly,
                Limit = request.Limit,
            };

            logger.LogDebug(
                "Query criteria. CurrentOnly: {CurrentOnly} RequiredScope: {RequiredScope} ExcludedScopes: {ExcludedScopes} AsOf: {AsOf} Limit: {Limit}",
                criteria.CurrentOnly,
                criteria.RequiredScopeDimension,
                criteria.ExcludedScopeDimensions.Count,
                criteria.AsOf,
                criteria.Limit);

            IReadOnlyList<CheapMemory> items = await search.SearchAsync(criteria, cancellationToken);

            logger.LogInformation("Query memories completed. Count: {Count}", items.Count);
            return new Response(items);
        }
    }
}
