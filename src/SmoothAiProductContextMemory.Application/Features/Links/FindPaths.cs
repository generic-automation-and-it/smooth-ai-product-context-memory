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

namespace SmoothAiProductContextMemory.Application.Features.Links;

/// <summary>
/// Provenance reconstruction: the chain of relationships connecting one memory to another, or to
/// everything it reaches within a bound.
/// </summary>
/// <remarks>
/// The handler resolves identity and the scope rule; the traversal and the endpoint memories' fields
/// come back from one composed statement in <see cref="IMemoryTraversal"/>. Depth is required on the
/// wire — an unbounded path query over a growing store is how a graph feature becomes an incident, and
/// nothing in the storage layer prevents it.
/// </remarks>
public static class FindPaths
{
    public sealed record Request(
        Guid SourceUuid,
        int MaxDepth,
        Guid? TargetUuid = null,
        string? Relation = null,
        string? Direction = null,
        string? Kind = null,
        string? Status = null,
        string? ScopeDimension = null,
        int Limit = MemorySearchDefaults.Limit) : IRequest<Response>;

    public sealed record Response(IReadOnlyList<PathResult> Paths);

    public sealed record PathResult(int Depth, IReadOnlyList<Hop> Hops, CheapMemory Endpoint);

    public sealed record Hop(Guid SourceUuid, Guid TargetUuid, string Relation, string Reason);

    /// <summary>Wire spellings of <see cref="TraversalDirection"/>. Absent means <c>outbound</c>.</summary>
    public static class DirectionValue
    {
        public const string Outbound = "outbound";

        public const string Inbound = "inbound";

        public const string Either = "either";

        public static readonly string[] All = [Outbound, Inbound, Either];
    }

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.SourceUuid).NotEmpty();
            // An omitted target walks everything reachable; an all-zero one is a caller mistake that
            // would otherwise return an empty path list and look like "no connection found".
            RuleFor(x => x.TargetUuid)
                .NotEqual(Guid.Empty)
                .When(x => x.TargetUuid.HasValue)
                .WithMessage("TargetUuid must be a real uuid when supplied; omit it to walk every reachable memory.");
            RuleFor(x => x.MaxDepth)
                .InclusiveBetween(1, MemoryTraversalDefaults.MaxDepth)
                .WithMessage($"MaxDepth must be between 1 and {MemoryTraversalDefaults.MaxDepth}.");
            RuleFor(x => x.Relation).MaximumLength(32);
            RuleFor(x => x.Kind).MaximumLength(64);
            RuleFor(x => x.Status).MaximumLength(32);
            RuleFor(x => x.ScopeDimension).MaximumLength(32);
            RuleFor(x => x.Limit).InclusiveBetween(1, MemorySearchDefaults.MaxLimit);
            RuleFor(x => x.Direction)
                .Must(direction => direction is null || DirectionValue.All.Contains(direction, StringComparer.Ordinal))
                .WithMessage($"Direction must be one of: {string.Join(", ", DirectionValue.All)}.");
        }
    }

    public sealed class Handler(
        IApplicationDbContext db,
        IMemoryTraversal traversal,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Find paths started");

            Memory source = await db.Memories
                .AsNoTracking()
                .Include(m => m.Group)
                .SingleOrDefaultAsync(m => m.Uuid == request.SourceUuid, cancellationToken)
                ?? throw new NotFoundException($"Memory '{request.SourceUuid}' was not found.");

            string dimension = source.Group!.ScopeDimension;
            if (!MemoryScopeFilter.IncludeGroup(dimension, request.ScopeDimension, hasGroupContext: false))
            {
                logger.LogDebug("Traversal blocked by scope. Dimension: {Dimension}", dimension);
                throw new ForbiddenException(
                    $"This memory is '{dimension}'-scoped. Request it with scope '{dimension}' to traverse from it.");
            }

            MemoryScopeFilter.ScopeFilterPlan scope =
                MemoryScopeFilter.Plan(request.ScopeDimension, hasGroupContext: false);

            var query = new MemoryPathQuery
            {
                SourceUuid = request.SourceUuid,
                TargetUuid = request.TargetUuid,
                MaxDepth = request.MaxDepth,
                Relation = string.IsNullOrWhiteSpace(request.Relation) ? null : request.Relation,
                Direction = ToDirection(request.Direction),
                Kind = string.IsNullOrWhiteSpace(request.Kind) ? null : request.Kind,
                Status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status,
                RequiredScopeDimension = scope.RequiredDimension,
                // Not scope.ExcludedDimensions: that list is empty for every explicit dimension,
                // because there RequiredDimension does the narrowing. Endpoints are narrowed by
                // RequiredScopeDimension; the hops a path crosses need their own visibility rule, or
                // declaring a scope would disclose more than declaring none.
                ExcludedScopeDimensions =
                    MemoryScopeFilter.HiddenDimensions(request.ScopeDimension, hasGroupContext: false),
                Limit = request.Limit,
            };

            logger.LogDebug(
                "Traversal bounds. MaxDepth: {MaxDepth} Direction: {Direction} Relation: {Relation} Endpoint: {HasEndpoint} Limit: {Limit}",
                query.MaxDepth,
                query.Direction,
                query.Relation,
                query.TargetUuid is not null,
                query.Limit);

            IReadOnlyList<MemoryPath> paths = await traversal.FindPathsAsync(query, cancellationToken);

            PathResult[] results =
            [
                .. paths.Select(path => new PathResult(
                    path.Depth,
                    [.. path.Hops.Select(hop => new Hop(hop.SourceUuid, hop.TargetUuid, hop.Relation, hop.Reason))],
                    path.Endpoint))
            ];

            logger.LogInformation("Find paths completed. Count: {Count}", results.Length);
            return new Response(results);
        }

        private static TraversalDirection ToDirection(string? direction) => direction switch
        {
            DirectionValue.Inbound => TraversalDirection.Inbound,
            DirectionValue.Either => TraversalDirection.Either,
            _ => TraversalDirection.Outbound,
        };
    }
}
