using SmoothAiProductContextMemory.Domain;

namespace SmoothAiProductContextMemory.Application.Features.Export;

/// <summary>
/// Deterministic folder and file names for the Markdown export tree.
/// </summary>
public static class ExportPaths
{
    public const string GroupsDirectory = "groups";
    public const string GroupFileName = "_group.md";
    public const string MarkerFileName = ".context-memory-export";
    public const string MemoryFilePrefix = "mem-";

    public sealed record GroupInput(Guid Uuid, string? CurrentDescriptionName);

    public sealed record MemoryInput(Guid Uuid, string SubjectSlug);

    public static IReadOnlyDictionary<Guid, string> AssignGroupFolders(IEnumerable<GroupInput> groups)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var assigned = new Dictionary<Guid, string>();

        foreach (GroupInput group in groups.OrderBy(g => g.Uuid))
        {
            string slug = GroupSlug(group);
            string folder = slug;
            if (!used.Add(folder))
            {
                folder = $"{slug}--{CollisionSuffix(group.Uuid)}";
                used.Add(folder);
            }

            assigned[group.Uuid] = folder;
        }

        return assigned;
    }

    public static IReadOnlyDictionary<Guid, string> AssignMemoryFiles(IEnumerable<MemoryInput> memories)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var assigned = new Dictionary<Guid, string>();

        foreach (MemoryInput memory in memories
                     .OrderBy(m => m.SubjectSlug, StringComparer.Ordinal)
                     .ThenBy(m => m.Uuid))
        {
            string fileName = $"{MemoryFilePrefix}{memory.SubjectSlug}.md";
            if (!used.Add(fileName))
            {
                fileName = $"{MemoryFilePrefix}{memory.SubjectSlug}--{CollisionSuffix(memory.Uuid)}.md";
                used.Add(fileName);
            }

            assigned[memory.Uuid] = fileName;
        }

        return assigned;
    }

    public static string GroupFile(string groupFolder) =>
        $"{GroupsDirectory}/{groupFolder}/{GroupFileName}";

    public static string MemoryFile(string groupFolder, string fileName) =>
        $"{GroupsDirectory}/{groupFolder}/{fileName}";

    public static string CollisionSuffix(Guid uuid) => uuid.ToString("N")[..8];

    private static string GroupSlug(GroupInput group)
    {
        if (Slug.TrySubject(group.CurrentDescriptionName, out string? slug))
        {
            return slug;
        }

        return $"group-{group.Uuid:D}";
    }
}
