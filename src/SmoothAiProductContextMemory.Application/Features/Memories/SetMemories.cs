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

    public sealed record ItemResult(Guid Uuid, string? BlobAddress, bool Versioned);

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
            RuleFor(x => x.Items).NotNull();
            RuleForEach(x => x.Items).ChildRules(item =>
            {
                item.RuleFor(i => i.Name).NotEmpty().MaximumLength(200);
                item.RuleFor(i => i.Description).NotEmpty();
                item.RuleFor(i => i.Statement).NotEmpty();
                item.RuleFor(i => i.Kind).NotEmpty().MaximumLength(64);
                item.RuleFor(i => i.Status)
                    .Must(s => s is MemoryVersion.MemoryVersionStatus.Proposed
                        or MemoryVersion.MemoryVersionStatus.Approved)
                    .WithMessage("Status must be proposed or approved.");
                item.RuleFor(i => i.Confidence).InclusiveBetween((short)0, (short)100);
            });
            RuleForEach(x => x.Links).ChildRules(link =>
            {
                link.RuleFor(l => l.SourceUuid).NotEmpty();
                link.RuleFor(l => l.TargetUuid).NotEmpty();
                link.RuleFor(l => l.Relation).NotEmpty();
                link.RuleFor(l => l.Reason).NotEmpty();
                link.RuleFor(l => l)
                    .Must(l => l.SourceUuid != l.TargetUuid)
                    .WithMessage("A link cannot target itself.");
            });
        }
    }

    public sealed class Handler(
        IApplicationDbContext db,
        IBlobStorage blobStorage,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Set memories started. Count: {Count} DryRun: {DryRun}", request.Items.Count, request.DryRun);

            MemoryGroup group = await db.MemoryGroups
                .SingleOrDefaultAsync(g => g.Uuid == request.GroupUuid, cancellationToken)
                ?? throw new NotFoundException($"Group '{request.GroupUuid}' was not found.");

            if (request.DryRun)
            {
                Response dry = await BuildDryRunAsync(request, group, cancellationToken);
                logger.LogInformation("Set memories completed. Created: {Created} Versioned: {Versioned}", dry.Created, dry.Versioned);
                return dry;
            }

            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var results = new List<ItemResult>();
            int created = 0;
            int versioned = 0;
            var assigned = new Dictionary<Guid, long>();

            try
            {
                foreach (MemoryWrite item in request.Items)
                {
                    ItemResult result = item.Uuid is { } existing
                        ? await VersionExistingAsync(existing, item, assigned, cancellationToken)
                        : await CreateNewAsync(group, item, assigned, cancellationToken);

                    if (result.Versioned)
                    {
                        versioned++;
                    }
                    else
                    {
                        created++;
                    }

                    results.Add(result);
                }

                int linked = await WriteLinksAsync(request.Links, assigned, cancellationToken);
                int labels = await ProposeLabelsAsync(request.LabelsProposed, cancellationToken);

                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "Set memories completed. Created: {Created} Versioned: {Versioned} Linked: {Linked}",
                    created,
                    versioned,
                    linked);

                return new Response(created, versioned, linked, 0, 0, labels, results);
            }
            catch (DbUpdateException ex)
            {
                await tx.RollbackAsync(cancellationToken);
                throw DbExceptionMapping.Map(ex);
            }
        }

        private async Task<Response> BuildDryRunAsync(Request request, MemoryGroup group, CancellationToken cancellationToken)
        {
            var results = new List<ItemResult>();
            int created = 0;
            int versioned = 0;

            foreach (MemoryWrite item in request.Items)
            {
                if (item.Uuid is { } uuid)
                {
                    bool exists = await db.Memories.AnyAsync(m => m.Uuid == uuid, cancellationToken);
                    if (!exists)
                    {
                        throw new NotFoundException($"Memory '{uuid}' was not found.");
                    }

                    versioned++;
                    results.Add(new ItemResult(uuid, null, true));
                }
                else
                {
                    created++;
                    results.Add(new ItemResult(Guid.NewGuid(), null, false));
                }
            }

            _ = group;
            int labels = request.LabelsProposed?.Count ?? 0;
            int linked = request.Links?.Count ?? 0;
            return new Response(created, versioned, linked, 0, 0, labels, results);
        }

        private async Task<ItemResult> CreateNewAsync(
            MemoryGroup group,
            MemoryWrite item,
            Dictionary<Guid, long> assigned,
            CancellationToken cancellationToken)
        {
            Guid uuid = item.Uuid ?? Guid.NewGuid();
            string? address = await StoreBlobAsync(item.Content, cancellationToken);

            var memory = new Memory
            {
                Uuid = uuid,
                LineageId = uuid,
                GroupId = group.Id,
                Name = item.Name,
                Description = item.Description,
                SubjectSlug = Slug.Subject(item.Description),
                Tags = item.Tags?.ToList() ?? [],
                Facets = item.Facets?.ToList() ?? [],
            };

            db.Memories.Add(memory);
            await db.SaveChangesAsync(cancellationToken);
            assigned[uuid] = memory.Id;

            db.MemoryVersions.Add(BuildVersion(memory.Id, 1, isCurrent: true, item, address));
            return new ItemResult(uuid, address, false);
        }

        private async Task<ItemResult> VersionExistingAsync(
            Guid uuid,
            MemoryWrite item,
            Dictionary<Guid, long> assigned,
            CancellationToken cancellationToken)
        {
            Memory memory = await db.Memories.SingleOrDefaultAsync(m => m.Uuid == uuid, cancellationToken)
                ?? throw new NotFoundException($"Memory '{uuid}' was not found.");

            assigned[uuid] = memory.Id;

            MemoryVersion current = await db.MemoryVersions
                .SingleOrDefaultAsync(v => v.MemoryId == memory.Id && v.IsCurrent, cancellationToken)
                ?? throw new ConflictException($"Memory '{uuid}' has no current version.");

            current.IsCurrent = false;
            await db.SaveChangesAsync(cancellationToken);

            string? address = await StoreBlobAsync(item.Content, cancellationToken);
            int next = current.Version + 1;
            db.MemoryVersions.Add(BuildVersion(memory.Id, next, isCurrent: true, item, address));
            return new ItemResult(uuid, address, true);
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
            long memoryId,
            int version,
            bool isCurrent,
            MemoryWrite item,
            string? blobAddress) => new()
        {
            MemoryId = memoryId,
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

        private async Task<int> WriteLinksAsync(
            IReadOnlyList<LinkWrite>? links,
            Dictionary<Guid, long> assigned,
            CancellationToken cancellationToken)
        {
            if (links is not { Count: > 0 })
            {
                return 0;
            }

            int count = 0;
            foreach (LinkWrite link in links)
            {
                long sourceId = await ResolveMemoryIdAsync(link.SourceUuid, assigned, cancellationToken);
                long targetId = await ResolveMemoryIdAsync(link.TargetUuid, assigned, cancellationToken);

                bool exists = await db.MemoryLinks.AnyAsync(
                    l => l.SourceMemoryId == sourceId && l.TargetMemoryId == targetId && l.Relation == link.Relation,
                    cancellationToken);
                if (exists)
                {
                    throw new ConflictException("Link already exists.");
                }

                db.MemoryLinks.Add(new MemoryLink
                {
                    SourceMemoryId = sourceId,
                    TargetMemoryId = targetId,
                    Relation = link.Relation,
                    Reason = link.Reason,
                });
                count++;
            }

            return count;
        }

        private async Task<long> ResolveMemoryIdAsync(
            Guid uuid,
            Dictionary<Guid, long> assigned,
            CancellationToken cancellationToken)
        {
            if (assigned.TryGetValue(uuid, out long id))
            {
                return id;
            }

            Memory memory = await db.Memories.SingleOrDefaultAsync(m => m.Uuid == uuid, cancellationToken)
                ?? throw new NotFoundException($"Memory '{uuid}' was not found.");
            return memory.Id;
        }

        private async Task<int> ProposeLabelsAsync(IReadOnlyList<string>? names, CancellationToken cancellationToken)
        {
            if (names is not { Count: > 0 })
            {
                return 0;
            }

            int proposed = 0;
            foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool exists = await db.Labels.AnyAsync(l => l.Name == name, cancellationToken);
                if (exists)
                {
                    continue;
                }

                db.Labels.Add(new Label
                {
                    Name = name,
                    Status = Label.LabelStatus.Draft,
                });
                proposed++;
            }

            return proposed;
        }
    }
}
