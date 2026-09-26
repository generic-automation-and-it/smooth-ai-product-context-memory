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
        string? SummaryPromptVersion,
        Guid? CreateUuid = null);

    public sealed record LinkWrite(Guid SourceUuid, Guid TargetUuid, string Relation, string Reason);

    public sealed record Request(
        Guid GroupUuid,
        IReadOnlyList<MemoryWrite> Items,
        IReadOnlyList<LinkWrite>? Links,
        IReadOnlyList<string>? LabelsProposed,
        bool DryRun = false) : IRequest<Response>;

    /// <summary>
    /// <see cref="Uuid"/> is stable on dry-run when the caller supplied <c>createUuid</c>; legacy
    /// creates keep a null dry-run identity and mint it only during persistence.
    /// </summary>
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
            RuleFor(x => x.Items)
                .Must(i => i is null || i.Count <= 200)
                .WithMessage("At most 200 items may be written per request.");
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
                item.RuleFor(i => i.Uuid)
                    .NotEqual(Guid.Empty)
                    .When(i => i.Uuid is not null);
                item.RuleFor(i => i.CreateUuid)
                    .NotEqual(Guid.Empty)
                    .When(i => i.CreateUuid is not null);
                item.RuleFor(i => i)
                    .Must(i => i.Uuid is null || i.CreateUuid is null)
                    .WithMessage("Uuid and CreateUuid cannot both be supplied.");
                item.RuleFor(i => i.ValidUntil)
                    .GreaterThan(i => i.ValidFrom)
                    .When(i => i.ValidUntil is not null)
                    .WithMessage("ValidUntil must be after ValidFrom.");
            });
            RuleFor(x => x.Links)
                .Must(links => links is null || links.Count <= 400)
                .WithMessage("At most 400 links may be written per request.");
            RuleForEach(x => x.Links).ChildRules(link =>
            {
                link.RuleFor(l => l.SourceUuid).NotEmpty();
                link.RuleFor(l => l.TargetUuid).NotEmpty();
                link.RuleFor(l => l.Relation).NotEmpty().MaximumLength(32);
                link.RuleFor(l => l.Reason).NotEmpty().MaximumLength(4000);
                link.RuleFor(l => l)
                    .Must(l => l.SourceUuid != l.TargetUuid)
                    .WithMessage("A link cannot target itself.");
            });
            RuleFor(x => x.LabelsProposed)
                .Must(l => l is null || l.Count <= 100)
                .WithMessage("At most 100 labels may be proposed per request.");
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
        MemoryVersion? CurrentVersion,
        int? NextVersion);

    private sealed record PlannedLink(LinkWrite Write, bool Skip);

    private sealed record WritePlan(
        MemoryGroup Group,
        IReadOnlyList<PlannedItem> Items,
        IReadOnlyList<PlannedLink> Links,
        IReadOnlyList<string> LabelsToInsert);

    public sealed class Handler(
        IApplicationDbContext db,
        IMemoryGraph graph,
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
                "Set memories completed. Created: {Created} Versioned: {Versioned} Linked: {Linked} Diverged: {Diverged} Skipped: {Skipped} DryRun: {DryRun}",
                response.Created,
                response.Versioned,
                response.Linked,
                response.Diverged,
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
            var plannedVersionTargets = new Dictionary<Guid, (Memory Memory, MemoryVersion Current, int NextVersion)>();
            var plannedCreateUuids = new HashSet<Guid>();

            // The plan resolves every write against stored state in one pass. The per-item checks below
            // are mere set lookups; the stored state they consult is fetched here in four batched
            // queries instead of once per item, which keeps a 200-item write at four round-trips
            // instead of up to ~800.
            Guid[] targetUuids = request.Items
                .Where(i => i.Uuid is not null)
                .Select(i => i.Uuid!.Value)
                .Distinct()
                .ToArray();
            Guid[] createUuids = request.Items
                .Where(i => i.Uuid is null && i.CreateUuid is not null)
                .Select(i => i.CreateUuid!.Value)
                .Distinct()
                .ToArray();
            string[] newSubjectSlugs = request.Items
                .Where(i => i.Uuid is null)
                .Select(i => Slug.Subject(i.Description))
                .Distinct()
                .ToArray();

            Dictionary<Guid, Memory> memoriesByUuid = (targetUuids.Length == 0
                    ? []
                    : await db.Memories
                        .Where(m => targetUuids.Contains(m.Uuid))
                        .ToArrayAsync(cancellationToken))
                .ToDictionary(m => m.Uuid);

            long[] targetMemoryIds = memoriesByUuid.Values.Select(m => m.Id).ToArray();
            Dictionary<long, MemoryVersion> currentByMemoryId = (targetMemoryIds.Length == 0
                    ? []
                    : await db.MemoryVersions
                        .Where(v => targetMemoryIds.Contains(v.MemoryId) && v.IsCurrent)
                        .ToArrayAsync(cancellationToken))
                .ToDictionary(v => v.MemoryId);

            HashSet<Guid> takenCreateUuids = createUuids.Length == 0
                ? []
                : (await db.Memories
                    .Where(m => createUuids.Contains(m.Uuid))
                    .Select(m => m.Uuid)
                    .ToArrayAsync(cancellationToken)).ToHashSet();

            HashSet<string> takenSubjectSlugs = newSubjectSlugs.Length == 0
                ? []
                : (await db.Memories
                    .Where(m => m.GroupId == group.Id && newSubjectSlugs.Contains(m.SubjectSlug))
                    .Select(m => m.SubjectSlug)
                    .ToArrayAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

            for (int index = 0; index < request.Items.Count; index++)
            {
                MemoryWrite item = request.Items[index];
                string slug = Slug.Subject(item.Description);

                if (item.Uuid is { } target)
                {
                    if (!plannedVersionTargets.TryGetValue(target, out var versionTarget))
                    {
                        if (!memoriesByUuid.TryGetValue(target, out Memory? memory))
                        {
                            throw new NotFoundException($"Memory '{target}' was not found.");
                        }

                        if (!currentByMemoryId.TryGetValue(memory.Id, out MemoryVersion? current))
                        {
                            throw new ConflictException($"Memory '{target}' has no current version.");
                        }

                        versionTarget = (memory, current, current.Version + 1);
                    }

                    logger.LogDebug("Planned version bump. Index: {Index} NextVersion: {Version}", index, versionTarget.NextVersion);
                    items.Add(new PlannedItem(
                        ItemMode.Version,
                        item,
                        slug,
                        target,
                        versionTarget.Memory.Id,
                        versionTarget.Current,
                        versionTarget.NextVersion));
                    plannedVersionTargets[target] = (
                        versionTarget.Memory,
                        versionTarget.Current,
                        versionTarget.NextVersion + 1);
                    continue;
                }

                if (!plannedSlugs.Add(slug))
                {
                    throw new ConflictException(
                        $"Two items in this batch share subject '{slug}'. Merge them or send one as a version bump.");
                }

                if (item.CreateUuid is { } createUuid)
                {
                    if (!plannedCreateUuids.Add(createUuid))
                    {
                        throw new ConflictException(
                            $"CreateUuid '{createUuid}' is used twice in this batch.");
                    }

                    if (takenCreateUuids.Contains(createUuid))
                    {
                        throw new ConflictException(
                            $"CreateUuid '{createUuid}' already belongs to an existing memory.");
                    }
                }

                if (takenSubjectSlugs.Contains(slug))
                {
                    throw new ConflictException(
                        $"Subject '{slug}' already exists in this group. Send it as a version bump.");
                }

                logger.LogDebug("Planned create. Index: {Index}", index);
                items.Add(new PlannedItem(ItemMode.Create, item, slug, item.CreateUuid, null, null, null));
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

            HashSet<Guid> batchUuids = items
                .Where(i => i.Uuid is not null)
                .Select(i => i.Uuid!.Value)
                .ToHashSet();

            // External link endpoints (not created in this batch) are resolved against the store in one
            // query, so the per-link check below is a set lookup rather than an AnyAsync per endpoint.
            HashSet<Guid> externalUuids = links
                .SelectMany(l => new[] { l.SourceUuid, l.TargetUuid })
                .Where(u => !batchUuids.Contains(u))
                .ToHashSet();
            HashSet<Guid> existingUuids = externalUuids.Count == 0
                ? []
                : (await db.Memories
                    .Where(m => externalUuids.Contains(m.Uuid))
                    .Select(m => m.Uuid)
                    .ToArrayAsync(cancellationToken)).ToHashSet();

            var planned = new List<PlannedLink>(links.Count);
            var seen = new HashSet<(Guid Source, Guid Target, string Relation)>();

            foreach (LinkWrite link in links)
            {
                EnsureMemoryExists(link.SourceUuid, batchUuids, existingUuids);
                EnsureMemoryExists(link.TargetUuid, batchUuids, existingUuids);

                bool duplicateInBatch = !seen.Add((link.SourceUuid, link.TargetUuid, link.Relation));
                bool duplicateInStore = !duplicateInBatch
                    && await graph.ExistsAsync(link.SourceUuid, link.TargetUuid, link.Relation, cancellationToken);

                bool skip = duplicateInBatch || duplicateInStore;
                if (skip)
                {
                    logger.LogDebug("Planned link skip. Relation: {Relation} InBatch: {InBatch}", link.Relation, duplicateInBatch);
                }

                planned.Add(new PlannedLink(link, skip));
            }

            return planned;
        }

        private static void EnsureMemoryExists(
            Guid uuid,
            HashSet<Guid> batchUuids,
            HashSet<Guid> existingUuids)
        {
            if (batchUuids.Contains(uuid) || existingUuids.Contains(uuid))
            {
                return;
            }

            throw new NotFoundException($"Memory '{uuid}' was not found.");
        }

        private async Task<IReadOnlyList<string>> PlanLabelsAsync(
            IReadOnlyList<string>? names,
            CancellationToken cancellationToken)
        {
            if (names is not { Count: > 0 })
            {
                return [];
            }

            string[] namesDistinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            HashSet<string> existingNames = namesDistinct.Length == 0
                ? []
                : (await db.Labels
                    .Where(l => namesDistinct.Contains(l.Name))
                    .Select(l => l.Name)
                    .ToArrayAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var toInsert = new List<string>();
            foreach (string name in namesDistinct)
            {
                if (!existingNames.Contains(name))
                {
                    toInsert.Add(name);
                }
            }

            return toInsert;
        }

        /// <summary>The dry-run verdict — plan counts and resolved identities, with no blob.</summary>
        private static Response Predict(WritePlan plan) =>
            new(
                plan.Items.Count(i => i.Mode == ItemMode.Create),
                plan.Items.Count(i => i.Mode == ItemMode.Version),
                plan.Links.Count(l => !l.Skip),
                plan.Items.Count(i => i.Mode == ItemMode.Create && i.Write.Kind == MemoryVersion.KindValue.Divergence),
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
                var currentVersions = new Dictionary<Guid, MemoryVersion>();

                for (int index = 0; index < plan.Items.Count; index++)
                {
                    PlannedItem item = plan.Items[index];
                    string? address = addresses.GetValueOrDefault(index);

                    results.Add(item.Mode == ItemMode.Create
                        ? CreateNew(plan.Group, item, address)
                        : await VersionExistingAsync(item, address, currentVersions, cancellationToken));
                }

                foreach (string name in plan.LabelsToInsert)
                {
                    db.Labels.Add(new Label { Name = name, Status = Label.LabelStatus.Draft });
                }

                await errorMapper.SaveOrMapAsync(() => db.SaveChangesAsync(cancellationToken));

                int linked = 0;
                int skipped = plan.Links.Count(l => l.Skip);
                foreach (PlannedLink link in plan.Links.Where(l => !l.Skip))
                {
                    bool created = await graph.CreateAsync(
                        link.Write.SourceUuid,
                        link.Write.TargetUuid,
                        link.Write.Relation,
                        link.Write.Reason,
                        cancellationToken);
                    linked += created ? 1 : 0;
                    skipped += created ? 0 : 1;
                }

                await transaction.CommitAsync(cancellationToken);

                return new Response(
                    plan.Items.Count(i => i.Mode == ItemMode.Create),
                    plan.Items.Count(i => i.Mode == ItemMode.Version),
                    linked,
                    plan.Items.Count(i => i.Mode == ItemMode.Create && i.Write.Kind == MemoryVersion.KindValue.Divergence),
                    skipped,
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
            Guid uuid = item.Uuid ?? Guid.NewGuid();
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
            Dictionary<Guid, MemoryVersion> currentVersions,
            CancellationToken cancellationToken)
        {
            Guid uuid = item.Uuid!.Value;
            MemoryVersion current = currentVersions.GetValueOrDefault(uuid) ?? item.CurrentVersion!;

            // Ordered, not merely atomic: the partial unique index forbids two currents, so the flip
            // must reach the database before the new current is inserted. Nothing forbids zero
            // currents, which is why both statements sit inside the caller's transaction.
            current.IsCurrent = false;
            await errorMapper.SaveOrMapAsync(() => db.SaveChangesAsync(cancellationToken));

            MemoryVersion next = BuildVersion(item.NextVersion!.Value, isCurrent: true, item.Write, blobAddress);
            next.MemoryId = item.MemoryId!.Value;
            db.MemoryVersions.Add(next);
            currentVersions[uuid] = next;

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
