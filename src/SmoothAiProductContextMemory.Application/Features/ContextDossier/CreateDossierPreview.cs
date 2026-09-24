using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Retrieval;

namespace SmoothAiProductContextMemory.Application.Features.ContextDossier;

/// <summary>
/// The manifest served without bodies and without composition (HLD-005 NFR-03 / LADR-14): effective
/// scope, selected volume, reach limits and an estimated composition cost. Hydrates no blob and
/// composes nothing, so scope and cost can be judged before either is paid for.
/// </summary>
public static class CreateDossierPreview
{
    public sealed record Request(
        string? Repo,
        string? InitiativeName,
        string? TicketProvider,
        string? TicketKey,
        IReadOnlyList<string>? Tags,
        string? Kind,
        string? Status,
        string? ScopeDimension,
        bool IncludeHistory,
        DateTimeOffset? AsOf,
        int WidenDepth) : IRequest<Response>;

    public sealed record Response(
        DossierSelectionPlan Selection,
        DossierVolume Volume,
        DossierReach Reach,
        DossierCostEstimate Cost,
        IReadOnlyList<DossierLimitHit> LimitsHit,
        bool NoMatch);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.WidenDepth).InclusiveBetween(1, MemoryTraversalDefaults.MaxDepth)
                .WithMessage($"WidenDepth must be between 1 and {MemoryTraversalDefaults.MaxDepth}.");
            RuleFor(x => x.TicketKey).NotEmpty().When(x => !string.IsNullOrWhiteSpace(x.TicketProvider));
            RuleFor(x => x.TicketProvider).NotEmpty().When(x => !string.IsNullOrWhiteSpace(x.TicketKey));
            RuleFor(x => x.ScopeDimension).MaximumLength(32);
            RuleFor(x => x.Kind).MaximumLength(64);
            RuleFor(x => x.Status).MaximumLength(32);
            RuleFor(x => x.ScopeDimension).Must(v => v is null || !v.Contains('\0'));
            RuleFor(x => x.Kind).Must(v => v is null || !v.Contains('\0'));
        }
    }

    public sealed class Handler(
        IMemorySearch search,
        IMemoryTraversal traversal,
        ITicketGraph ticketGraph,
        IMemoryGraph graph,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Context dossier preview started");

            DossierAnchor anchor = DossierSelection.AnchorFrom(
                request.Repo,
                request.InitiativeName,
                request.TicketProvider,
                request.TicketKey,
                request.Tags,
                request.Kind,
                request.Status,
                request.ScopeDimension,
                request.IncludeHistory,
                request.AsOf,
                request.WidenDepth);

            DossierSelectionResult selection = await DossierSelection.ResolveAsync(
                search, traversal, ticketGraph, graph, anchor, cancellationToken);

            DossierSelectionPlan plan = DossierSelection.BuildPlan(anchor);
            DossierVolume volume = new(
                selection.Selected.Count,
                selection.AnchorCount,
                selection.WidenedCount,
                selection.EdgeCount);

            DossierReach reach = new(
                anchor.WidenDepth,
                selection.AnchorCount,
                selection.WidenedCount,
                selection.Selected.Count,
                selection.EdgeCount,
                selection.HiddenPathDropped);

            var limitsHit = new List<DossierLimitHit>();
            if (selection.DepthLimitReached)
            {
                limitsHit.Add(new DossierLimitHit(DossierOmissionReason.DepthReached, anchor.WidenDepth));
            }

            if (selection.LimitReached)
            {
                limitsHit.Add(new DossierLimitHit(DossierOmissionReason.CapReached, anchor.ItemLimit));
            }

            DossierCostEstimate cost = new(
                MonetaryAvailable: false,
                MonetaryCost: null,
                Assumptions:
                    "Composition cost scales with the slice: the selected count and the edge count above are the drivers. "
                    + "Widening depth and the item cap bound the slice before composition. ",
                Uncertainty:
                    "No composition is performed here, so the estimate is the slice size, not a measured cost. "
                    + "Monetary cost is unavailable — no pricing provider is configured.");

            logger.LogInformation(
                "Context dossier preview completed. Selected: {Selected} Widened: {Widened} Edges: {Edges}",
                selection.Selected.Count, selection.WidenedCount, selection.EdgeCount);

            return new Response(plan, volume, reach, cost, [.. limitsHit], NoMatch: selection.Selected.Count == 0);
        }
    }
}
