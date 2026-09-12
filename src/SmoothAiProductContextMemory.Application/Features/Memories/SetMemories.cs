using System.Text;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Memories;

/// <summary>
/// The single transactional write. Owns <c>is_current</c>.
/// </summary>
/// <remarks>
/// Dry run and write share one plan. Every decision that can make the write fail or skip — subject
/// collision, missing version target, unknown link endpoint, already-present link, already-present
/// label — is taken in <see cref="Handler.BuildPlanAsync"/>, which touches no state. The persist step
/// only executes the plan. A dry run that reached its verdict by a shorter route would stop
/// predicting the write, and this endpoint is the caller's only pre-write veto point.
/// </remarks>
public static class SetMemories
{
    public sealed record MemoryWrite(
        Guid? Uuid,
        string Name,
        string Description,
        string Statement,
        string ContentSummary,
        string Kind,
        IReadOnlyList<string>? Facets,
        IReadOnlyList<string>? Tags,
        string Status,
        short Confidence,
        string? Content,
        IReadOnlyList<SourceInput>? Sources,
        DateTimeOffset ValidFrom,
        DateTimeOffset? ValidUntil,
        string? SummaryModel,
        string? SummaryPromptVersion);

    public sealed record LinkWrite(Guid SourceUuid, Guid TargetUuid, string Relation, string Reason);

    public sealed record Request(
        Guid GroupUuid,
        IReadOnlyList<MemoryWrite> Items,
        IReadOnlyList<LinkWrite>? Links,
        IReadOnlyList<string>? LabelsProposed,
        bool DryRun = false) : IRequest<Response>;

    /// <summary><see cref="Uuid"/> is null for a planned create on a dry run — the identity is minted at persist time.</summary>
    public sealed record ItemResult(Guid? Uuid, string? BlobAddress, bool Versioned);

    public sealed record Response(
        int Created,
        int Versioned,
        int Linked,
        int Diverged,
        int Skipped,
        int LabelsProposed,
        IReadOnlyList<ItemResult> Items);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.GroupUuid).NotEmpty();
            RuleFor(x => x.Items).NotNull().NotEmpty();
            RuleForEach(x => x.Items).ChildRules(item =>
            {
                item.RuleFor(i => i.Name).NotEmpty().MaximumLength(200);
                item.RuleFor(i => i.Description).NotEmpty();
                item.RuleFor(i => i.Description)
                    .Must(d => Slug.TrySubject(d, out _))
                    .WithMessage("Description must contain at least one letter or digit.");
                item.RuleFor(i => i.Statement).NotEmpty();
                item.RuleFor(i => i.Kind).NotEmpty().MaximumLength(64);
                item.RuleFor(i => i.Status)
                    .Must(s => s is MemoryVersion.MemoryVersionStatus.Proposed
                        or MemoryVersion.MemoryVersionStatus.Approved)
                    .WithMessage("Status must be proposed or approved.");
                item.RuleFor(i => i.Confidence).InclusiveBetween((short)0, (short)100);
                item.RuleFor(i => i.ValidUntil)
                    .GreaterThan(i => i.ValidFrom)
                    .When(i => i.ValidUntil is not null)
                    .WithMessage("ValidUntil must be after ValidFrom.");
            });
            RuleForEach(x => x.Links).ChildRules(link =>
            {
                link.RuleFor(l => l.SourceUuid).NotEmpty();
                link.RuleFor(l => l.TargetUuid).NotEmpty();
                link.RuleFor(l => l.Relation).NotEmpty().MaximumLength(32);
                link.RuleFor(l => l.Reason).NotEmpty();
                link.RuleFor(l => l)
                    .Must(l => l.SourceUuid != l.TargetUuid)
                    .WithMessage("A link cannot target itself.");
            });
            RuleForEach(x => x.LabelsProposed)
                .NotEmpty()
                .MaximumLength(100);
        }
    }

    private enum ItemMode
    {
        Create,
        Version,
    }

    private sealed record PlannedItem(
        ItemMode Mode,
        MemoryWrite Write,
        string SubjectSlug,
        Guid? Uuid,
        long? MemoryId,
        MemoryVersion? CurrentVersion);

    private sealed record PlannedLink(LinkWrite Write, long? SourceMemoryId, long? TargetMemoryId, bool Skip);

    private sealed record WritePlan(
        MemoryGroup Group,
        IReadOnlyList<PlannedItem> Items,
        IReadOnlyList<PlannedLink> Links,
        IReadOnlyList<string> LabelsToInsert);

    public sealed class Handler(
        IApplicationDbContext db,
        IBlobStorage blobStorage,
        IDbErrorMapper errorMapper,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation(
                "Set memories started. Count: {Count} DryRun: {DryRun}",
                request.Items.Count,
                request.DryRun);

            MemoryGroup group = await db.MemoryGroups
                .SingleOrDefaultAsync(g => g.Uuid == request.GroupUuid, cancellationToken)
                ?? throw new NotFoundException($"Group '{request.GroupUuid}' was not found.");

            WritePlan plan = await BuildPlanAsync(request, group, cancellationToken);

            Response response = request.DryRun
                ? Predict(plan)
                : await PersistAsync(plan, cancellationToken);

            logger.LogInformation(
                "Set memories completed. Created: {Created} Versioned: {Versioned} Linked: {Linked} Skipped: {Skipped} DryRun: {DryRun}",
                response.Created,
                response.Versioned,
                response.Linked,
                response.Skipped,
                request.DryRun);

            return response;
        }

        /// <summary>
        /// Resolves every write against stored state without mutating anything. Shared by dry run and
        /// persist so the two cannot diverge.
        /// </summary>
        private async Task<WritePlan> BuildPlanAsync(
            Request request,
            MemoryGroup group,
            CancellationToken cancellationToken)
        {
            var items = new List<PlannedItem>(request.Items.Count);
            var plannedSlugs = new HashSet<string>(StringComparer.Ordinal);
            var plannedVersionTargets = new HashSet<Guid>();

            for (int index = 0; index < request.Items.Count; index++)
            {
                MemoryWrite item = request.Items[index];
                string slug = Slug.Subject(item.Description);

                if (item.Uuid is { } target)
                {
                    if (!plannedVersionTargets.Add(target))
                    {
                        throw new ConflictException(
                            $"Memory '{target}' is versioned twice in this batch. Merge the items or send them separately.");
                    }

                    Memory memory = await db.Memories
                        .SingleOrDefaultAsync(m => m.Uuid == target, cancellationToken)
                        ?? throw new NotFoundException($"Memory '{target}' was not found.");

                    MemoryVersion current = await db.MemoryVersions
                        .SingleOrDefaultAsync(v => v.MemoryId == memory.Id && v.IsCurrent, cancellationToken)
                        ?? throw new ConflictException($"Memory '{target}' has no current version.");

                    logger.LogDebug("Planned version bump. Index: {Index} NextVersion: {Version}", index, current.Version + 1);
                    items.Add(new PlannedItem(ItemMode.Version, item, slug, target, memory.Id, current));
                    continue;
                }

                if (!plannedSlugs.Add(slug))
                {
                    throw new ConflictException(
                        $"Two items in this batch share subject '{slug}'. Merge them or send one as a version bump.");
                }

                bool subjectTaken = await db.Memories
                    .AnyAsync(m => m.GroupId == group.Id && m.SubjectSlug == slug, cancellationToken);
                if (subjectTaken)
                {
                    throw new ConflictException(
                        $"Subject '{slug}' already exists in this group. Send it as a version bump.");
                }

                logger.LogDebug("Planned create. Index: {Index}", index);
                items.Add(new PlannedItem(ItemMode.Create, item, slug, null, null, null));
            }

            IReadOnlyList<PlannedLink> links = await PlanLinksAsync(request.Links, items, cancellationToken);
            IReadOnlyList<string> labels = await PlanLabelsAsync(request.LabelsProposed, cancellationToken);

            return new WritePlan(group, items, links, labels);
        }

        private async Task<IReadOnlyList<PlannedLink>> PlanLinksAsync(
            IReadOnlyList<LinkWrite>? links,
            IReadOnlyList<PlannedItem> items,
            CancellationToken cancellationToken)
        {
            if (links is not { Count: > 0 })
            {
                return [];
            }

            Dictionary<Guid, long> batchIds = items
                .Where(i => i.Uuid is not null && i.MemoryId is not null)
                .ToDictionary(i => i.Uuid!.Value, i => i.MemoryId!.Value);

            var planned = new List<PlannedLink>(links.Count);
            var seen = new HashSet<(Guid Source, Guid Target, string Relation)>();

            foreach (LinkWrite link in links)
            {
                long source = await ResolveMemoryIdAsync(link.SourceUuid, batchIds, cancellationToken);
                long target = await ResolveMemoryIdAsync(link.TargetUuid, batchIds, cancellationToken);

                bool duplicateInBatch = !seen.Add((link.SourceUuid, link.TargetUuid, link.Relation));
                bool duplicateInStore = !duplicateInBatch && await db.MemoryLinks.AnyAsync(
                    l => l.SourceMemoryId == source
                        && l.TargetMemoryId == target
                        && l.Relation == link.Relation,
                    cancellationToken);

                bool skip = duplicateInBatch || duplicateInStore;
                if (skip)
                {
                    logger.LogDebug("Planned link skip. Relation: {Relation} InBatch: {InBatch}", link.Relation, duplicateInBatch);
                }

                planned.Add(new PlannedLink(link, source, target, skip));
            }

            return planned;
        }

        private async Task<long> ResolveMemoryIdAsync(
            Guid uuid,
            Dictionary<Guid, long> batchIds,
            CancellationToken cancellationToken)
        {
            if (batchIds.TryGetValue(uuid, out long id))
            {
                return id;
            }

            Memory memory = await db.Memories
                .SingleOrDefaultAsync(m => m.Uuid == uuid, cancellationToken)
                ?? throw new NotFoundException($"Memory '{uuid}' was not found.");
            return memory.Id;
        }

        private async Task<IReadOnlyList<string>> PlanLabelsAsync(
            IReadOnlyList<string>? names,
            CancellationToken cancellationToken)
        {
            if (names is not { Count: > 0 })
            {
                return [];
            }

            var toInsert = new List<string>();
            foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool exists = await db.Labels.AnyAsync(l => l.Name == name, cancellationToken);
                if (!exists)
                {
                    toInsert.Add(name);
                }
            }

            return toInsert;
        }

        /// <summary>The dry-run verdict — the plan's own counts, with no identity and no blob.</summary>
        private static Response Predict(WritePlan plan) =>
            new(
                plan.Items.Count(i => i.Mode == ItemMode.Create),
                plan.Items.Count(i => i.Mode == ItemMode.Version),
                plan.Links.Count(l => !l.Skip),
                0,
                plan.Links.Count(l => l.Skip),
                plan.LabelsToInsert.Count,
                [.. plan.Items.Select(i => new ItemResult(i.Uuid, null, i.Mode == ItemMode.Version))]);

        private async Task<Response> PersistAsync(WritePlan plan, CancellationToken cancellationToken)
        {
            // Blobs are content-addressed and immutable, so they are stored before the transaction
            // opens: the write is idempotent on retry and the transaction never stays open across
            // object-storage round trips. A rolled-back transaction leaves an unreferenced object,
            // which is the cheaper failure — deleting is unsafe, addresses are shared.
            var addresses = new Dictionary<int, string>();
            for (int index = 0; index < plan.Items.Count; index++)
            {
                string? address = await StoreBlobAsync(plan.Items[index].Write.Content, cancellationToken);
                if (address is not null)
                {
                    addresses[index] = address;
                }
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var results = new List<ItemResult>(plan.Items.Count);

                for (int index = 0; index < plan.Items.Count; index++)
                {
                    PlannedItem item = plan.Items[index];
                    string? address = addresses.GetValueOrDefault(index);

                    results.Add(item.Mode == ItemMode.Create
                        ? CreateNew(plan.Group, item, address)
                        : await VersionExistingAsync(item, address, cancellationToken));
                }

                foreach (PlannedLink link in plan.Links.Where(l => !l.Skip))
                {
                    db.MemoryLinks.Add(new MemoryLink
                    {
                        SourceMemoryId = link.SourceMemoryId!.Value,
                        TargetMemoryId = link.TargetMemoryId!.Value,
                        Relation = link.Write.Relation,
                        Reason = link.Write.Reason,
                    });
                }

                foreach (string name in plan.LabelsToInsert)
                {
                    db.Labels.Add(new Label { Name = name, Status = Label.LabelStatus.Draft });
                }

                await errorMapper.SaveOrMapAsync(() => db.SaveChangesAsync(cancellationToken));
                await transaction.CommitAsync(cancellationToken);

                return new Response(
                    plan.Items.Count(i => i.Mode == ItemMode.Create),
                    plan.Items.Count(i => i.Mode == ItemMode.Version),
                    plan.Links.Count(l => !l.Skip),
                    0,
                    plan.Links.Count(l => l.Skip),
                    plan.LabelsToInsert.Count,
                    results);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        private ItemResult CreateNew(MemoryGroup group, PlannedItem item, string? blobAddress)
        {
            Guid uuid = Guid.NewGuid();
            var memory = new Memory
            {
                Uuid = uuid,
                LineageId = uuid,
                GroupId = group.Id,
                Name = item.Write.Name,
                Description = item.Write.Description,
                SubjectSlug = item.SubjectSlug,
                Tags = item.Write.Tags?.ToList() ?? [],
                Facets = item.Write.Facets?.ToList() ?? [],
            };

            db.Memories.Add(memory);

            // Navigation, not the surrogate key: EF fixes up memory_id on insert, so no intermediate
            // SaveChanges is needed to learn it.
            MemoryVersion version = BuildVersion(1, isCurrent: true, item.Write, blobAddress);
            version.Memory = memory;
            db.MemoryVersions.Add(version);

            return new ItemResult(uuid, blobAddress, false);
        }

        private async Task<ItemResult> VersionExistingAsync(
            PlannedItem item,
            string? blobAddress,
            CancellationToken cancellationToken)
        {
            MemoryVersion current = item.CurrentVersion!;

            // Ordered, not merely atomic: the partial unique index forbids two currents, so the flip
            // must reach the database before the new current is inserted. Nothing forbids zero
            // currents, which is why both statements sit inside the caller's transaction.
            current.IsCurrent = false;
            await errorMapper.SaveOrMapAsync(() => db.SaveChangesAsync(cancellationToken));

            MemoryVersion next = BuildVersion(current.Version + 1, isCurrent: true, item.Write, blobAddress);
            next.MemoryId = item.MemoryId!.Value;
            db.MemoryVersions.Add(next);

            return new ItemResult(item.Uuid, blobAddress, true);
        }

        private async Task<string?> StoreBlobAsync(string? content, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(content))
            {
                return null;
            }

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            return await blobStorage.StoreAsync(stream, "text/plain; charset=utf-8", cancellationToken);
        }

        private static MemoryVersion BuildVersion(
            int version,
            bool isCurrent,
            MemoryWrite item,
            string? blobAddress) => new()
        {
            Version = version,
            IsCurrent = isCurrent,
            Statement = item.Statement,
            ContentSummary = item.ContentSummary,
            BlobAddress = blobAddress,
            Kind = item.Kind,
            Confidence = item.Confidence,
            Status = item.Status,
            Sources = item.Sources?
                .Select(s => SourceDocument.Create(s.Kind, s.Reference, s.CapturedAt))
                .ToList() ?? [],
            ValidFrom = item.ValidFrom,
            ValidUntil = item.ValidUntil,
            CreatedOn = DateTimeOffset.UtcNow,
            SummaryStamp = string.IsNullOrWhiteSpace(item.SummaryModel)
                ? null
                : SummaryStampDocument.Create(item.SummaryModel, item.SummaryPromptVersion ?? string.Empty),
        };
    }
}
