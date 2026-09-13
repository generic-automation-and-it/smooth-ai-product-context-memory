using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Export;

/// <summary>
/// Generated Markdown dump of the store. Forensic: bypasses <c>MemoryScopeFilter</c>.
/// </summary>
public static class ExportStore
{
    public sealed record Request(string OutputDirectory, bool IncludeHistory = false, bool Force = false)
        : IRequest<Response>;

    public sealed record Response(int Groups, int Memories, int FilesWritten, int MissingBlobs, int NonTextBlobs);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.OutputDirectory).NotEmpty().MaximumLength(1024);
        }
    }

    public sealed class Handler(
        IApplicationDbContext db,
        IBlobStorage blobStorage,
        IMarkdownExportSink sink,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Markdown export started");

            List<MemoryGroup> groups = await db.MemoryGroups
                .AsNoTracking()
                .Include(g => g.Initiative)
                .Include(g => g.Descriptions)
                .ToListAsync(cancellationToken);

            List<Memory> memories = await db.Memories
                .AsNoTracking()
                .Include(m => m.Versions)
                .ToListAsync(cancellationToken);

            List<MemoryLink> links = await db.MemoryLinks
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            Dictionary<long, Memory> memoriesById = memories.ToDictionary(m => m.Id);
            Dictionary<long, MemoryGroup> groupsById = groups.ToDictionary(g => g.Id);

            IReadOnlyDictionary<Guid, string> groupFolders = ExportPaths.AssignGroupFolders(
                groups.Select(g => new ExportPaths.GroupInput(g.Uuid, CurrentDescription(g)?.Name)));

            await sink.PrepareAsync(request.OutputDirectory, request.Force, cancellationToken);

            int filesWritten = 0;
            int missingBlobs = 0;
            int nonTextBlobs = 0;
            int memoriesWritten = 0;

            foreach (MemoryGroup group in groups.OrderBy(g => g.Uuid))
            {
                logger.LogDebug("Exporting group {GroupUuid}", group.Uuid);

                string folder = groupFolders[group.Uuid];
                ExportGroupDocument groupDocument = ToGroupDocument(group);
                await sink.WriteFileAsync(
                    ExportPaths.GroupFile(folder),
                    ExportRenderer.RenderGroup(groupDocument, request.IncludeHistory),
                    cancellationToken);
                filesWritten++;

                List<Memory> groupMemories = memories
                    .Where(m => m.GroupId == group.Id)
                    .OrderBy(m => m.SubjectSlug, StringComparer.Ordinal)
                    .ThenBy(m => m.Uuid)
                    .ToList();

                IReadOnlyDictionary<Guid, string> memoryFiles = ExportPaths.AssignMemoryFiles(
                    groupMemories.Select(m => new ExportPaths.MemoryInput(m.Uuid, m.SubjectSlug)));

                foreach (Memory memory in groupMemories)
                {
                    MemoryVersion? current = memory.Versions.SingleOrDefault(v => v.IsCurrent);
                    if (current is null)
                    {
                        logger.LogWarning("Export skipped memory with no current version. Memory: {MemoryUuid}", memory.Uuid);
                        continue;
                    }

                    logger.LogDebug("Exporting memory {MemoryUuid}", memory.Uuid);

                    var historical = new List<ExportVersionBody>();
                    IEnumerable<MemoryVersion> versions = request.IncludeHistory
                        ? memory.Versions.OrderByDescending(v => v.Version)
                        : [current];

                    ExportVersionBody? currentBody = null;
                    foreach (MemoryVersion version in versions)
                    {
                        (ExportVersionBody body, int missing, int nonText) = await ReadVersionAsync(
                            memory.Uuid,
                            version,
                            cancellationToken);
                        missingBlobs += missing;
                        nonTextBlobs += nonText;

                        if (version.IsCurrent)
                        {
                            currentBody = body;
                        }
                        else
                        {
                            historical.Add(body);
                        }
                    }

                    var document = new ExportMemoryDocument(
                        memory.Uuid,
                        memory.LineageId,
                        group.Uuid,
                        memory.SubjectSlug,
                        memory.Name,
                        memory.Description,
                        group.ScopeDimension,
                        group.ScopeIdentifier,
                        memory.Facets.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                        memory.Tags.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                        currentBody!,
                        historical,
                        CollectLinks(memory, memoriesById, groupsById, links));

                    await sink.WriteFileAsync(
                        ExportPaths.MemoryFile(folder, memoryFiles[memory.Uuid]),
                        ExportRenderer.RenderMemory(document, request.IncludeHistory),
                        cancellationToken);
                    filesWritten++;
                    memoriesWritten++;
                }
            }

            logger.LogInformation(
                "Markdown export completed. Groups: {GroupCount} Memories: {MemoryCount} Files: {FileCount} MissingBlobs: {MissingBlobCount}",
                groups.Count,
                memoriesWritten,
                filesWritten,
                missingBlobs);

            return new Response(groups.Count, memoriesWritten, filesWritten, missingBlobs, nonTextBlobs);
        }

        private async Task<(ExportVersionBody Body, int Missing, int NonText)> ReadVersionAsync(
            Guid memoryUuid,
            MemoryVersion version,
            CancellationToken cancellationToken)
        {
            BlobRenderState state = BlobRenderState.None;
            string? text = null;
            int missing = 0;
            int nonText = 0;

            if (!string.IsNullOrWhiteSpace(version.BlobAddress))
            {
                BlobContent? blob = await blobStorage.GetAsync(version.BlobAddress, cancellationToken);
                if (blob is null)
                {
                    state = BlobRenderState.Missing;
                    missing = 1;
                    logger.LogWarning(
                        "Export blob missing. Memory: {MemoryUuid} Version: {Version}",
                        memoryUuid,
                        version.Version);
                }
                else
                {
                    await using (blob)
                    {
                        (state, text) = await ExportBlobReader.ReadAsync(blob, cancellationToken);
                    }

                    if (state == BlobRenderState.NonText)
                    {
                        nonText = 1;
                        logger.LogWarning(
                            "Export blob is not text. Memory: {MemoryUuid} Version: {Version}",
                            memoryUuid,
                            version.Version);
                    }
                }
            }

            var body = new ExportVersionBody(
                version.Version,
                version.IsCurrent,
                version.Kind,
                version.Status,
                version.Confidence,
                version.ValidFrom,
                version.ValidUntil,
                version.CreatedOn,
                version.Statement,
                version.ContentSummary,
                version.Sources
                    .OrderBy(s => s.Kind, StringComparer.Ordinal)
                    .ThenBy(s => s.Reference, StringComparer.Ordinal)
                    .Select(s => new ExportSource(s.Kind, s.Reference, s.CapturedAt))
                    .ToArray(),
                state,
                text);

            return (body, missing, nonText);
        }

        private static ExportGroupDocument ToGroupDocument(MemoryGroup group)
        {
            ExportGroupDescription? current = CurrentDescription(group) is { } row
                ? new ExportGroupDescription(row.Version, row.Name, row.Body, row.CreatedOn)
                : null;

            IReadOnlyList<ExportGroupDescription> history = group.Descriptions
                .Where(d => current is null || d.Version != current.Version)
                .OrderByDescending(d => d.Version)
                .Select(d => new ExportGroupDescription(d.Version, d.Name, d.Body, d.CreatedOn))
                .ToArray();

            IReadOnlyList<ExportTicket> tickets = group.Tickets
                .OrderBy(t => t.Key, StringComparer.Ordinal)
                .ThenBy(t => t.Provider, StringComparer.Ordinal)
                .Select(t => new ExportTicket(t.Provider, t.Key, t.Url))
                .ToArray();

            return new ExportGroupDocument(
                group.Uuid,
                group.ScopeDimension,
                group.ScopeIdentifier,
                group.Initiative?.Name ?? string.Empty,
                group.Initiative?.Status ?? string.Empty,
                group.Repo,
                group.RepoUrl,
                tickets,
                current,
                history);
        }

        private static GroupDescription? CurrentDescription(MemoryGroup group) =>
            group.Descriptions.OrderByDescending(d => d.Version).FirstOrDefault();

        private static IReadOnlyList<ExportLink> CollectLinks(
            Memory memory,
            IReadOnlyDictionary<long, Memory> memoriesById,
            IReadOnlyDictionary<long, MemoryGroup> groupsById,
            IReadOnlyList<MemoryLink> links)
        {
            var result = new List<ExportLink>();

            foreach (MemoryLink link in links.Where(l => l.SourceMemoryId == memory.Id).OrderBy(l => l.TargetMemoryId))
            {
                if (!TryResolve(link.TargetMemoryId, memoriesById, groupsById, out Memory? other, out MemoryGroup? otherGroup))
                {
                    continue;
                }

                result.Add(new ExportLink(
                    "outgoing",
                    link.Relation,
                    link.Reason,
                    other.Uuid,
                    other.Description,
                    otherGroup.Uuid));
            }

            foreach (MemoryLink link in links.Where(l => l.TargetMemoryId == memory.Id).OrderBy(l => l.SourceMemoryId))
            {
                if (!TryResolve(link.SourceMemoryId, memoriesById, groupsById, out Memory? other, out MemoryGroup? otherGroup))
                {
                    continue;
                }

                result.Add(new ExportLink(
                    "incoming",
                    link.Relation,
                    link.Reason,
                    other.Uuid,
                    other.Description,
                    otherGroup.Uuid));
            }

            return result;
        }

        private static bool TryResolve(
            long memoryId,
            IReadOnlyDictionary<long, Memory> memoriesById,
            IReadOnlyDictionary<long, MemoryGroup> groupsById,
            out Memory other,
            out MemoryGroup otherGroup)
        {
            if (memoriesById.TryGetValue(memoryId, out Memory? memory)
                && groupsById.TryGetValue(memory.GroupId, out MemoryGroup? group))
            {
                other = memory;
                otherGroup = group;
                return true;
            }

            other = null!;
            otherGroup = null!;
            return false;
        }
    }
}
