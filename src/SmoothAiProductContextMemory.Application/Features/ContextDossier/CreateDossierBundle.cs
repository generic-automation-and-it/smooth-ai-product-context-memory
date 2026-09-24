using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Application.Features.Export;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.ContextDossier;

/// <summary>
/// The deterministic half of the dossier read (HLD-005 LADR-02): assemble the selected memories with
/// their hydrated bodies, the edges between them, and a manifest recording the effective selection,
/// what was reached and what was cut. Never writes; never calls a model; never composes.
/// </summary>
public static class CreateDossierBundle
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

    public sealed record Response(DossierBundle Bundle);

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
        IApplicationDbContext db,
        IMemorySearch search,
        IMemoryTraversal traversal,
        ITicketGraph ticketGraph,
        IMemoryGraph graph,
        IBlobStorage blobStorage,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Context dossier bundle started");

            DossierAnchor anchor = BuildAnchor(request);
            DossierSelectionResult selection = await DossierSelection.ResolveAsync(
                search, traversal, ticketGraph, graph, anchor, cancellationToken);

            if (selection.Selected.Count == 0)
            {
                logger.LogInformation("Context dossier bundle completed. No match.");
                DossierManifest emptyManifest = BuildManifest(anchor, selection, selectedCount: 0, limitsHit: []);
                return new Response(new DossierBundle([], [], [], emptyManifest with { NoMatch = true }));
            }

            // Load the full version rows (blob address + citation) for the selected memory identities.
            Guid[] uuids = [.. selection.Selected.Select(c => c.Memory.Uuid)];
            MemoryVersion[] rows = await db.MemoryVersions
                .AsNoTracking()
                .Include(v => v.Memory)
                    .ThenInclude(m => m!.Group)
                .Where(v => v.Memory != null && uuids.Contains(v.Memory.Uuid))
                .ToArrayAsync(cancellationToken);

            List<DossierMemory> items = [];
            var omitted = new List<DossierOmittedItem>();

            // Hydrate and classify each selected claim's current version, then (with history) the
            // historical ones. Cut-on-cap is decided by the ordering, never by traversal arrival
            // (NFR-02), so the selected set is ordered first and only then trimmed.
            foreach (SelectedClaim claim in selection.Selected)
            {
                MemoryVersion[] versionRows = rows
                    .Where(v => v.Memory!.Uuid == claim.Memory.Uuid)
                    .OrderBy(v => v.Version)
                    .ToArray();

                foreach (MemoryVersion row in versionRows)
                {
                    if (!request.IncludeHistory && !row.IsCurrent)
                    {
                        continue;
                    }

                    (DossierMemory? memory, DossierOmittedItem? omission) =
                        await BuildItemAsync(row, claim, request, cancellationToken);
                    if (memory is not null)
                    {
                        items.Add(memory);
                    }
                    else if (omission is not null)
                    {
                        omitted.Add(omission);
                    }
                }
            }

            // Deterministic ordering at every level (selected memories, edges, manifest entries).
            items = [.. items.OrderBy(i => i, DossierMemoryCompare.Instance)];

            // Mechanical collapse of the same blob reached under distinct memories (LADR-05): the
            // later one collapses into the earlier, reported as omitted — never silently dropped.
            (items, omitted) = CollapseSameBlob(items, omitted);

            // Cut-on-cap decided by the ordering above. Only the present items are trimmed — the
            // already-accounted omissions (collapsed / unreadable) never consume the present budget,
            // and counting them into the trigger would make the tail-cut overlap the kept set and
            // double-count an item into both items and omitted.
            bool capReached = items.Count > anchor.ItemLimit;
            if (capReached)
            {
                int excess = items.Count - anchor.ItemLimit;
                var cut = items.TakeLast(excess).ToArray();
                omitted.AddRange(cut.Select(i => new DossierOmittedItem(i.Uuid, DossierOmissionReason.CapReached)));
                items = [.. items.Take(anchor.ItemLimit)];
            }

            IReadOnlyList<DossierEdge> edges = CollectEdges(selection);
            IReadOnlyList<DossierLimitHit> limitsHit = BuildLimitsHit(selection, items.Count, omitted.Count, anchor, capReached);

            DossierManifest manifest = BuildManifest(anchor, selection, items.Count + omitted.Count, limitsHit);

            logger.LogInformation(
                "Context dossier bundle completed. Items: {ItemCount} Omitted: {OmittedCount} Edges: {EdgeCount}",
                items.Count, omitted.Count, edges.Count);

            return new Response(new DossierBundle(
                [.. items], edges, [.. omitted], manifest));
        }

        private async Task<(DossierMemory? Item, DossierOmittedItem? Omission)> BuildItemAsync(
            MemoryVersion row,
            SelectedClaim claim,
            Request request,
            CancellationToken cancellationToken)
        {
            MemoryGroup group = row.Memory!.Group!;

            // Hydrate the body. An unreadable body is an omission with the unreadable reason
            // (NFR-04), never a silent skip.
            (string? bodyText, string state) = await HydrateAsync(row, cancellationToken);
            if (state == DossierBodyState.NonText)
            {
                return (null, new DossierOmittedItem(row.Memory!.Uuid, DossierOmissionReason.UnreadableBody));
            }

            string? reachedVia = string.Join(", ", claim.ReachedVia);
            var sources = row.Sources
                .OrderBy(s => s.Kind, StringComparer.Ordinal)
                .ThenBy(s => s.Reference, StringComparer.Ordinal)
                .Select(s => new DossierSource(s.Kind, s.Reference, s.CapturedAt))
                .ToArray();

            var memory = new DossierMemory(
                row.Memory!.Uuid,
                group.Uuid,
                row.Memory.Name,
                row.Memory.Description,
                row.Statement,
                row.ContentSummary,
                row.Kind,
                row.Status,
                row.Confidence,
                group.ScopeDimension,
                group.ScopeIdentifier,
                row.ValidFrom,
                row.ValidUntil,
                row.Version,
                row.IsCurrent,
                row.CreatedOn,
                sources,
                bodyText,
                state,
                [reachedVia]);

            return (memory, null);
        }

        private async Task<(string? Text, string State)> HydrateAsync(
            MemoryVersion row,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(row.BlobAddress))
            {
                return (null, DossierBodyState.None);
            }

            BlobContent? blob = await blobStorage.GetAsync(row.BlobAddress, cancellationToken);
            if (blob is null)
            {
                return (null, DossierBodyState.Missing);
            }

            await using (blob)
            {
                (BlobRenderState state, string? text) = await ExportBlobReader.ReadAsync(blob, cancellationToken);
                return state switch
                {
                    BlobRenderState.Inlined => (text, DossierBodyState.Inlined),
                    BlobRenderState.NonText => (null, DossierBodyState.NonText),
                    _ => (null, DossierBodyState.Missing),
                };
            }
        }

        internal static (List<DossierMemory>, List<DossierOmittedItem>) CollapseSameBlob(
            List<DossierMemory> items,
            List<DossierOmittedItem> omitted)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var kept = new List<DossierMemory>();
            foreach (DossierMemory item in items)
            {
                if (item.BodyState != DossierBodyState.Inlined || item.BodyText is null)
                {
                    kept.Add(item);
                    continue;
                }

                if (!seen.Add(item.BodyText))
                {
                    omitted.Add(new DossierOmittedItem(item.Uuid, DossierOmissionReason.CollapsedIntoAnotherClaim));
                }
                else
                {
                    kept.Add(item);
                }
            }

            return (kept, omitted);
        }

        private static IReadOnlyList<DossierEdge> CollectEdges(DossierSelectionResult selection)
        {
            // Edges among the selected memories, loaded once by the selection (F6). A selected
            // memory's relationship to a hidden one is absent, because the hidden memory is never
            // selected (NFR-01).
            return [.. selection.Edges
                .OrderBy(e => e.SourceUuid)
                .ThenBy(e => e.Relation, StringComparer.Ordinal)
                .ThenBy(e => e.TargetUuid)
                .Select(e => new DossierEdge(e.SourceUuid, e.TargetUuid, e.Relation, e.Reason))];
        }

        private static DossierLimitHit[] BuildLimitsHit(
            DossierSelectionResult selection,
            int itemCount,
            int omittedCount,
            DossierAnchor anchor,
            bool capReached)
        {
            var hits = new List<DossierLimitHit>();
            if (selection.DepthLimitReached)
            {
                hits.Add(new DossierLimitHit(DossierOmissionReason.DepthReached, anchor.WidenDepth));
            }

            // The selection path fetches at the item limit; when it hit that ceiling (LimitReached) it
            // cannot know whether more matched, so truncation must be reported, not silent (NFR-03,
            // NFR-04). The strict-exceeds case covers a selection that came back over the limit through a
            // union of anchors and widening. A history-inflated cut (many versions of a few selected
            // memories) exceeds the cap without the memory count reaching the fetch ceiling, so the cut
            // itself is the signal and is named too.
            if (capReached
                || selection.LimitReached
                || (selection.Selected.Count >= anchor.ItemLimit
                    && itemCount + omittedCount >= anchor.ItemLimit))
            {
                if (hits.All(h => h.Limit != DossierOmissionReason.CapReached))
                {
                    hits.Add(new DossierLimitHit(DossierOmissionReason.CapReached, anchor.ItemLimit));
                }
            }

            return [.. hits];
        }

        private static DossierManifest BuildManifest(
            DossierAnchor anchor,
            DossierSelectionResult selection,
            int selectedCount,
            IReadOnlyList<DossierLimitHit> limitsHit)
        {
            DossierSelectionPlan plan = DossierSelection.BuildPlan(anchor);

            DossierReach reach = new(
                anchor.WidenDepth,
                selection.AnchorCount,
                selection.WidenedCount,
                selection.Selected.Count,
                selection.EdgeCount,
                selection.HiddenPathDropped);

            return new DossierManifest(plan, selectedCount, reach, limitsHit, NoMatch: false);
        }

        private static DossierAnchor BuildAnchor(Request request) =>
            DossierSelection.AnchorFrom(
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

        private sealed class DossierMemoryCompare : IComparer<DossierMemory>
        {
            public static readonly DossierMemoryCompare Instance = new();

            public int Compare(DossierMemory? a, DossierMemory? b)
            {
                if (ReferenceEquals(a, b))
                {
                    return 0;
                }

                if (a is null)
                {
                    return -1;
                }

                if (b is null)
                {
                    return 1;
                }

                int validity = a.ValidFrom.CompareTo(b.ValidFrom);
                if (validity != 0)
                {
                    return validity;
                }

                int captured = a.CreatedOn.CompareTo(b.CreatedOn);
                if (captured != 0)
                {
                    return captured;
                }

                int identity = a.Uuid.CompareTo(b.Uuid);
                if (identity != 0)
                {
                    return identity;
                }

                return a.Version.CompareTo(b.Version);
            }
        }
    }
}
