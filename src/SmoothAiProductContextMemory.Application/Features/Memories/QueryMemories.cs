using NodaTime;
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
        Instant? AsOf = null,
        int Limit = MemorySearchDefaults.Limit,
        string FacetMatchMode = FacetMatchModeValue.Any,
        string? ProductName = null,
        bool Inspect = false,
        string RecallPurpose = Abstractions.RecallPurpose.DirectRetrieval,
        Guid? CallerRequestId = null) : IRequest<Response>;


    /// <summary>
    /// The retrieval response. <see cref="Inspection"/> is null for a normal retrieval (carrying the
    /// matched <see cref="Items"/>); non-null marks the response as inspection, with <see cref="Items"/>
    /// empty and the evidence set in <see cref="Inspection"/>.
    /// </summary>
    public sealed record Response(IReadOnlyList<CheapMemory> Items, Inspection? Inspection = null);

    /// <summary>
    /// The evidence a retrieval resolved and what it would match, without shipping the memory content.
    /// Produced only by <see cref="Request.Inspect"/>; it is never written as recall feedback and never
    /// skips scope enforcement — the same identity/traversal/scope resolution runs as for a retrieval.
    /// </summary>
    public sealed record Inspection(
        bool IsInspection,
        string? Note,
        string? FreeText,
        string? TsQuery,
        IReadOnlyList<string> Facets,
        IReadOnlyList<string> Tags,
        string FacetMatchMode,
        string? Kind,
        string? Status,
        string? RequiredScopeDimension,
        IReadOnlyList<string> ExcludedScopeDimensions,
        bool HasGroupContext,
        string? TicketProvider,
        string? TicketKey,
        Guid? OwnerGroupUuid,
        long? TicketGroupId,
        int ReferencedUuids,
        string? ReferencedRequiredScopeDimension,
        IReadOnlyList<string> ReferencedExcludedScopeDimensions,
        string? Repo,
        string? InitiativeName,
        IReadOnlyList<string>? ProductInitiatives,
        IReadOnlyList<string>? ProductRepos,
        bool CurrentOnly,
        bool IncludeProposed,
        Instant? AsOf,
        int Limit,
        int MatchedCount,
        bool Limited);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Kind).MaximumLength(64);
            RuleFor(x => x.Status).MaximumLength(32);
            RuleFor(x => x.ScopeDimension).MaximumLength(32);
            RuleFor(x => x.Limit).InclusiveBetween(1, MemorySearchDefaults.MaxLimit);
            RuleFor(x => x.RecallPurpose).Must(Abstractions.RecallPurpose.IsKnown);
            RuleFor(x => x.CallerRequestId).NotEqual(Guid.Empty).When(x => x.CallerRequestId is not null);
            RuleFor(x => x.FacetMatchMode)
                .Must(mode => FacetMatchModeValue.Allowed.Contains(mode, StringComparer.Ordinal))
                .WithMessage($"FacetMatchMode must be one of: {string.Join(", ", FacetMatchModeValue.Allowed)}.");
            RuleFor(x => x.TicketKey)
                .NotEmpty()
                .When(x => !string.IsNullOrWhiteSpace(x.TicketProvider))
                .WithMessage("TicketKey is required when TicketProvider is supplied.");
            RuleFor(x => x.TicketProvider)
                .NotEmpty()
                .When(x => !string.IsNullOrWhiteSpace(x.TicketKey))
                .WithMessage("TicketProvider is required when TicketKey is supplied.");
            RuleFor(x => x.ProductName)
                .NotEmpty()
                .MaximumLength(200)
                .Must(p => p.Trim() == p && !p.Any(char.IsControl))
                .When(x => !string.IsNullOrWhiteSpace(x.ProductName))
                .WithMessage("ProductName must not have leading/trailing whitespace or control characters.");
        }
    }

    public sealed class Handler(
        IApplicationDbContext db,
        IMemorySearch search,
        IRecallFeedback feedback,
        IProductGraph productGraph,
        IMemoryGraph graph,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Query memories started");

            string shape = RecallShapeClassifier.Classify(request);
            Guid retrievalId = Guid.NewGuid();
            Instant occurredOn = SystemClock.Instance.GetCurrentInstant();
            bool inspect = request.Inspect;

            bool hasTicket = !string.IsNullOrWhiteSpace(request.TicketProvider)
                && !string.IsNullOrWhiteSpace(request.TicketKey);
            bool hasGroupContext = request.GroupUuid is not null || hasTicket;

            long? groupId = null;
            Guid? ownerGroupUuid = null;
            IReadOnlyList<Guid>? referencedUuids = null;
            if (hasTicket)
            {
                MemoryGroup? ticketGroup = await TicketLookup.FindGroupByTicketAsync(
                    db, request.TicketProvider!, request.TicketKey!, cancellationToken);
                if (ticketGroup is null)
                {
                    // A miss is a signal the caller acts on, not an error. With no owner group there can
                    // be no ABOUT reference either (D1: the membership trigger owns the ticket vertex a
                    // reference requires), so this is empty by invariant.
                    if (inspect)
                    {
                        return new Response([], InspectFrom(request, null, null, null, null, null, null, null, "ticket-miss"));
                    }

                    logger.LogInformation("Query memories completed. Count: {Count}", 0);
                    Emit([new RecallFeedbackRecord(retrievalId, null, shape, occurredOn, request.RecallPurpose, request.CallerRequestId)]);
                    return new Response([]);
                }

                groupId = ticketGroup.Id;
                ownerGroupUuid = ticketGroup.Uuid;
                referencedUuids = await graph.ListAboutAsync(
                    request.TicketProvider!, request.TicketKey!, cancellationToken);
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
                FacetMatchMode = request.FacetMatchMode,
            };

            // The reference branch of the union is not in-group context, so it carries the
            // non-group-context scope plan: programme is hidden unless the caller asked for that
            // scope (D2). The owner group keeps group context. Both are pushed into SQL together.
            if (referencedUuids is { Count: > 0 })
            {
                MemoryScopeFilter.ScopeFilterPlan referencedScope =
                    MemoryScopeFilter.Plan(request.ScopeDimension, hasGroupContext: false);
                criteria = criteria with
                {
                    ReferencedUuids = referencedUuids,
                    ReferencedRequiredScopeDimension = referencedScope.RequiredDimension,
                    ReferencedExcludedScopeDimensions = referencedScope.ExcludedDimensions,
                };
            }

            // Product affinity is resolved through the graph first, then applied in SQL before the row
            // ceiling — an unknown product is a 404, never an empty result that looks like a miss.
            IReadOnlyList<string>? productInitiatives = null;
            IReadOnlyList<string>? productRepos = null;
            if (!string.IsNullOrWhiteSpace(request.ProductName))
            {
                if (!await productGraph.ExistsAsync(request.ProductName, cancellationToken))
                {
                    throw new NotFoundException(
                        $"Product '{request.ProductName}' is not registered. Register it with 'upsert-product-name' first.");
                }

                productInitiatives = await productGraph.GetInitiativeNamesAsync(request.ProductName, cancellationToken);
                productRepos = await productGraph.GetRepoNamesAsync(request.ProductName, cancellationToken);
                if (productInitiatives.Count == 0 && productRepos.Count == 0)
                {
                    if (inspect)
                    {
                        return new Response([], InspectFrom(request, criteria, null, ownerGroupUuid, groupId,
                            referencedUuids, null, null, "product-no-links"));
                    }

                    logger.LogInformation("Query memories completed. Count: {Count}", 0);
                    Emit([new RecallFeedbackRecord(retrievalId, null, shape, occurredOn)]);
                    return new Response([]);
                }

                criteria = criteria with
                {
                    InitiativeNames = productInitiatives,
                    RepoNames = productRepos,
                };
            }

            logger.LogDebug(
                "Query criteria. CurrentOnly: {CurrentOnly} RequiredScope: {RequiredScope} ExcludedScopes: {ExcludedScopes} AsOf: {AsOf} Limit: {Limit}",
                criteria.CurrentOnly,
                criteria.RequiredScopeDimension,
                criteria.ExcludedScopeDimensions.Count,
                criteria.AsOf,
                criteria.Limit);

            if (inspect)
            {
                MemorySearchInspection inspectResult = await search.InspectAsync(criteria, cancellationToken);
                logger.LogInformation("Query memories inspection completed. MatchedCount: {Count}",
                    inspectResult.MatchedCount);
                return new Response([], InspectFrom(request, criteria, inspectResult,
                    ownerGroupUuid, groupId, referencedUuids, productInitiatives, productRepos, null));
            }

            IReadOnlyList<CheapMemory> items = await search.SearchAsync(criteria, cancellationToken);

            // The ticket binding lives in two stores — the group's membership array and the graph's
            // ABOUT edges — so it is assembled after the rows are materialised, once, bounded by the page.
            items = await TicketIdentityLookup.EnrichAsync(db, graph, items, cancellationToken);

            if (productInitiatives is not null)
            {
                items = await EnrichProductMatchesAsync(items, productInitiatives, productRepos!, cancellationToken);
            }

            if (ownerGroupUuid is { } ownerUuid)
            {
                // A memory both in the owner group and referenced (refused by the write path, but
                // defended) is reported as the owner, per the union's ordering.
                items = [.. items.Select(i => i with { TicketMatch = i.GroupUuid == ownerUuid ? "owner" : "reference" })];
            }

            logger.LogInformation("Query memories completed. Count: {Count}", items.Count);

            // Feedback is emitted after the response is fully materialised, on a fresh connection
            // outside the retrieval's unit of work, and is guarded so a feedback failure can never
            // fail the retrieval; it cannot change the result set, ordering, limit or ranking.
            if (items.Count == 0)
            {
                Emit([new RecallFeedbackRecord(retrievalId, null, shape, occurredOn, request.RecallPurpose, request.CallerRequestId)]);
            }
            else
            {
                RecallFeedbackRecord[] records = new RecallFeedbackRecord[items.Count];
                for (int i = 0; i < items.Count; i++)
                {
                    records[i] = new RecallFeedbackRecord(retrievalId, items[i].Uuid, shape, occurredOn, request.RecallPurpose, request.CallerRequestId);
                }

                Emit(records);
            }

            return new Response(items);
        }

        private static Inspection InspectFrom(
            Request request,
            MemorySearchCriteria? criteria,
            MemorySearchInspection? result,
            Guid? ownerGroupUuid,
            long? groupId,
            IReadOnlyList<Guid>? referencedUuids,
            IReadOnlyList<string>? productInitiatives,
            IReadOnlyList<string>? productRepos,
            string? note)
        {
            // A miss path (ticket-miss, product-no-links) has no criteria and no search result: the
            // evidence is simply "no owner group" or "no product links". The matched count stays zero and
            // no predicate is reported as though it had run.
            MemorySearchCriteria resolved = criteria ?? new MemorySearchCriteria
            {
                FreeText = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query,
                Facets = request.Facets ?? [],
                Tags = request.Tags ?? [],
                Kind = string.IsNullOrWhiteSpace(request.Kind) ? null : request.Kind,
                Status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status,
                ExcludeProposed = !request.IncludeProposed,
                GroupUuid = request.GroupUuid,
                Repo = string.IsNullOrWhiteSpace(request.Repo) ? null : request.Repo,
                InitiativeName = string.IsNullOrWhiteSpace(request.InitiativeName) ? null : request.InitiativeName,
                AsOf = request.AsOf,
                CurrentOnly = request.CurrentOnly,
                Limit = request.Limit,
                FacetMatchMode = request.FacetMatchMode,
            };

            return new Inspection(
                IsInspection: true,
                Note: note,
                FreeText: resolved.FreeText,
                TsQuery: result?.TsQuery,
                Facets: resolved.Facets,
                Tags: resolved.Tags,
                FacetMatchMode: resolved.FacetMatchMode,
                Kind: resolved.Kind,
                Status: resolved.Status,
                RequiredScopeDimension: resolved.RequiredScopeDimension,
                ExcludedScopeDimensions: resolved.ExcludedScopeDimensions,
                HasGroupContext: resolved.GroupId is not null || resolved.GroupUuid is not null,
                TicketProvider: request.TicketProvider,
                TicketKey: request.TicketKey,
                OwnerGroupUuid: ownerGroupUuid,
                TicketGroupId: groupId,
                ReferencedUuids: referencedUuids?.Count ?? 0,
                ReferencedRequiredScopeDimension: criteria?.ReferencedRequiredScopeDimension,
                ReferencedExcludedScopeDimensions: criteria?.ReferencedExcludedScopeDimensions ?? [],
                Repo: resolved.Repo,
                InitiativeName: resolved.InitiativeName,
                ProductInitiatives: productInitiatives,
                ProductRepos: productRepos,
                CurrentOnly: resolved.CurrentOnly,
                IncludeProposed: !resolved.ExcludeProposed,
                AsOf: resolved.AsOf,
                Limit: resolved.Limit,
                MatchedCount: result?.MatchedCount ?? 0,
                Limited: result?.Limited ?? false);
        }

        private async Task<IReadOnlyList<CheapMemory>> EnrichProductMatchesAsync(
            IReadOnlyList<CheapMemory> items,
            IReadOnlyList<string> productInitiatives,
            IReadOnlyList<string> productRepos,
            CancellationToken cancellationToken)
        {
            if (items.Count == 0)
            {
                return items;
            }

            MemoryGroup[] groups = await db.MemoryGroups
                .AsNoTracking()
                .Where(g => items.Select(i => i.GroupUuid).Distinct().Contains(g.Uuid))
                .Select(g => new MemoryGroup { Uuid = g.Uuid, InitiativeId = g.InitiativeId, Repo = g.Repo })
                .ToArrayAsync(cancellationToken);
            Dictionary<Guid, MemoryGroup> groupsByUuid = groups.ToDictionary(g => g.Uuid);

            long[] initiativeIds = groups.Select(g => g.InitiativeId).Distinct().ToArray();
            Dictionary<long, string> initiativeNames = await db.Initiatives
                .AsNoTracking()
                .Where(i => initiativeIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => i.Name, cancellationToken);

            string[] distinctInitiativeNames = groups
                .Select(g => initiativeNames.GetValueOrDefault(g.InitiativeId))
                .OfType<string>()
                .Distinct()
                .ToArray();
            IReadOnlyDictionary<string, int> sharedCounts = await productGraph.CountProductsPerInitiativeAsync(
                distinctInitiativeNames, cancellationToken);

            var initiativeSet = new HashSet<string>(productInitiatives);
            var repoSet = new HashSet<string>(productRepos);

            return [.. items.Select(item =>
            {
                if (!groupsByUuid.TryGetValue(item.GroupUuid, out MemoryGroup? group))
                {
                    return item with { ProductMatch = null, SharedInitiative = false };
                }

                string? initiativeName = initiativeNames.GetValueOrDefault(group.InitiativeId);
                string? productMatch = initiativeName is not null && initiativeSet.Contains(initiativeName)
                    ? "initiative"
                    : group.Repo is not null && repoSet.Contains(group.Repo)
                        ? "repo"
                        : null;
                bool shared = initiativeName is not null && sharedCounts.GetValueOrDefault(initiativeName) > 1;
                return item with { ProductMatch = productMatch, SharedInitiative = shared };
            })];
        }

        private void Emit(RecallFeedbackRecord[] records)
        {
            try
            {
                feedback.Record(records);
            }
            catch (Exception ex)
            {
                // No record payload in the log — content must never reach a log line.
                logger.LogWarning(ex, "Recall feedback write failed; retrieval unaffected.");
            }
        }
    }
}
