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

    public sealed record MemoryInput(Guid Uuid, string SubjectSlug, int CurrentVersion);

    public static IReadOnlyDictionary<Guid, string> AssignGroupFolders(IEnumerable<GroupInput> groups)
    {
        // OrdinalIgnoreCase for the same reason as AssignMemoryFiles: a folder name is written to disk,
        // and two group names differing only by case are one directory on a case-insensitive
        // filesystem. Group names pass through `Slug.TrySubject`, which lower-cases, so this does not
        // fire in practice — the guard is here so it cannot start depending on that.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assigned = new Dictionary<Guid, string>();

        foreach (GroupInput group in groups.OrderBy(g => g.Uuid))
        {
            string slug = GroupSlug(group);
            string folder = slug;
            if (!used.Add(folder))
            {
                folder = $"{slug}--{CollisionSuffix(group.Uuid)}";
                if (!used.Add(folder))
                {
                    // Full uuid is unique per input — guaranteed terminal fallback.
                    folder = $"{slug}--{group.Uuid:N}";
                    if (!used.Add(folder))
                    {
                        throw new InvalidOperationException(
                            $"Refusing to assign export folder '{folder}' twice for group UUID {group.Uuid:D}.");
                    }
                }
            }

            assigned[group.Uuid] = folder;
        }

        return assigned;
    }

    public static IReadOnlyDictionary<Guid, string> AssignMemoryFiles(IEnumerable<MemoryInput> memories)
    {
        // OrdinalIgnoreCase, not Ordinal. These names go straight to disk, and on a case-insensitive
        // filesystem (the default on macOS and Windows) `mem-Foo.v1.md` and `mem-foo.v1.md` are one
        // file, so the second write silently replaces the first and no post-copy parity check can see
        // it — by then the destination *is* the source. An ordinal set treats them as distinct, skips
        // the collision suffix, and emits exactly the pair that collapses.
        //
        // Every stored `SubjectSlug` originates at `Slug.Subject`, which lower-cases, so in practice this
        // set never fires — that is why the defect survived. `MemoryInput.SubjectSlug` is an unvalidated
        // string, though, and `ExportStore` forwards the stored column verbatim, so an untrusted archive
        // restore or a direct database write can carry an unslugged value. The guard has to hold at the
        // boundary, not depend on a single producer upstream of it.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assigned = new Dictionary<Guid, string>();

        foreach (MemoryInput memory in memories
                     .OrderBy(m => m.SubjectSlug, StringComparer.Ordinal)
                     .ThenBy(m => m.Uuid))
        {
            // The filename carries the DB version (is_current), so the on-disk file reflects the
            // store's versioning: mem-<slug>.v<currentVersion>.md
            string fileName = $"{MemoryFilePrefix}{memory.SubjectSlug}.v{memory.CurrentVersion}.md";
            if (!used.Add(fileName))
            {
                fileName = $"{MemoryFilePrefix}{memory.SubjectSlug}.v{memory.CurrentVersion}--{CollisionSuffix(memory.Uuid)}.md";
                if (!used.Add(fileName))
                {
                    // Full uuid is unique per input — guaranteed terminal fallback.
                    fileName = $"{MemoryFilePrefix}{memory.SubjectSlug}.v{memory.CurrentVersion}--{memory.Uuid:N}.md";
                    if (!used.Add(fileName))
                    {
                        throw new InvalidOperationException(
                            $"Refusing to assign export file '{fileName}' twice for memory UUID {memory.Uuid:D}.");
                    }
                }
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
