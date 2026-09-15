using System.Text.Json.Serialization;
using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Retrieval;

namespace SmoothAiProductContextMemory.Application.Features.Tickets;

public static class FindTicketPaths
{
    public sealed record Request(
        TicketIdentity Anchor,
        [property: JsonNumberHandling(JsonNumberHandling.Strict)] int MaxDepth,
        string? Direction = null,
        string? ScopeDimension = null,
        string? Kind = null,
        int PathLimit = MemorySearchDefaults.Limit,
        int MemoryLimit = MemorySearchDefaults.Limit) : IRequest<TicketTraversalResult>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Anchor).NotNull().SetValidator(new SetTicketParent.IdentityValidator());
            RuleFor(x => x.MaxDepth).InclusiveBetween(1, 5);
            RuleFor(x => x.Direction)
                .Must(direction => direction is null or "outbound" or "inbound" or "either")
                .WithMessage("Direction must be outbound, inbound, or either.");
            RuleFor(x => x.ScopeDimension).MaximumLength(32);
            RuleFor(x => x.Kind).MaximumLength(64);
            RuleFor(x => x.PathLimit).InclusiveBetween(1, MemorySearchDefaults.MaxLimit);
            RuleFor(x => x.MemoryLimit).InclusiveBetween(1, MemorySearchDefaults.MaxLimit);
        }
    }

    public sealed class Handler(ITicketGraph graph, ILogger<Handler> logger)
        : IRequestHandler<Request, TicketTraversalResult>
    {
        public async ValueTask<TicketTraversalResult> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Find ticket paths started");
            var query = new TicketTraversalQuery
            {
                Anchor = request.Anchor,
                MaxDepth = request.MaxDepth,
                Direction = request.Direction switch
                {
                    "inbound" => TraversalDirection.Inbound,
                    "either" => TraversalDirection.Either,
                    _ => TraversalDirection.Outbound,
                },
                RequiredScopeDimension = MemoryScopeFilter.Plan(request.ScopeDimension, hasGroupContext: false)
                    .RequiredDimension,
                HiddenDimensions = MemoryScopeFilter.HiddenDimensions(request.ScopeDimension, hasGroupContext: false),
                Kind = string.IsNullOrWhiteSpace(request.Kind) ? null : request.Kind,
                PathLimit = request.PathLimit,
                MemoryLimit = request.MemoryLimit,
            };
            TicketTraversalResult result = await graph.TraverseAsync(query, cancellationToken);
            logger.LogInformation("Find ticket paths completed. Paths: {PathCount} Memories: {MemoryCount}",
                result.Paths.Count, result.Items.Count);
            return result;
        }
    }
}
