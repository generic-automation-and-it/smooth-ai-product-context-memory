using SmoothAiProductContextMemory.Application.Features.Export;
using SmoothAiProductContextMemory.Domain;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features.Export;

public class ExportPathsTests
{
    private static readonly Guid First = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Second = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Group_folder_slugs_from_current_description_name()
    {
        IReadOnlyDictionary<Guid, string> folders = ExportPaths.AssignGroupFolders(
        [
            new ExportPaths.GroupInput(First, "Persistence layer"),
        ]);

        folders[First].ShouldBe("persistence-layer");
    }

    [Fact]
    public void Group_without_name_uses_uuid()
    {
        IReadOnlyDictionary<Guid, string> folders = ExportPaths.AssignGroupFolders(
        [
            new ExportPaths.GroupInput(First, null),
        ]);

        folders[First].ShouldBe($"group-{First:D}");
    }

    [Fact]
    public void Group_name_collision_suffixes_later_uuid()
    {
        IReadOnlyDictionary<Guid, string> folders = ExportPaths.AssignGroupFolders(
        [
            new ExportPaths.GroupInput(Second, "Same Name"),
            new ExportPaths.GroupInput(First, "Same Name"),
        ]);

        folders[First].ShouldBe("same-name");
        folders[Second].ShouldBe($"same-name--{ExportPaths.CollisionSuffix(Second)}");
    }

    [Fact]
    public void Subjects_differing_only_by_case_stay_distinct_files()
    {
        // The export writes these names straight to disk, and on a case-insensitive filesystem two names
        // differing only by case are one file — the second write silently replaces the first, which no
        // check after the copy can see. `AssignMemoryFiles` is safe here only because every stored
        // SubjectSlug originates at `Slug.Subject`, which lower-cases each character; this pins that
        // property at the boundary the export actually uses, so a future caller passing an unslugged
        // value fails here rather than on someone's disk.
        //
        // `ShouldNotBe` on the two names is the whole assertion. An earlier version also counted them
        // under `StringComparer.OrdinalIgnoreCase`, which read as the case-insensitivity guard and was
        // not: mutating it to `Ordinal` left the suite green, because the two names differ by more than
        // case once the uuid suffix is applied. A second assertion restating the first is the failure
        // mode this repo has already paid for twice.
        IReadOnlyDictionary<Guid, string> files = ExportPaths.AssignMemoryFiles(
        [
            new ExportPaths.MemoryInput(First, Slug.Subject("Storage Engine"), CurrentVersion: 1),
            new ExportPaths.MemoryInput(Second, Slug.Subject("STORAGE engine"), CurrentVersion: 1),
        ]);

        // Two descriptions that fold together are one slug, so this is a genuine collision and the
        // uuid suffix must separate them rather than both claiming one filename.
        files[First].ShouldNotBe(files[Second]);
    }

    [Fact]
    public void Slugging_is_what_makes_case_collision_impossible()
    {
        // The negative control for the test above: without the slug, two subjects differing only by case
        // would produce the same filename and the ordinal set would keep them apart. Asserting the slug
        // collapses them is what makes the distinctness above a property of the design rather than luck.
        Slug.Subject("Storage Engine").ShouldBe(Slug.Subject("STORAGE engine"));
    }

    [Fact]
    public void Memory_files_use_mem_prefix_with_db_version()
    {
        IReadOnlyDictionary<Guid, string> files = ExportPaths.AssignMemoryFiles(
        [
            new ExportPaths.MemoryInput(First, "postgres-storage", CurrentVersion: 2),
        ]);

        files[First].ShouldBe("mem-postgres-storage.v2.md");
        ExportPaths.MemoryFile("persistence-layer", files[First])
            .ShouldBe("groups/persistence-layer/mem-postgres-storage.v2.md");
    }

    [Fact]
    public void Memory_file_collision_suffixes_later_uuid()
    {
        IReadOnlyDictionary<Guid, string> files = ExportPaths.AssignMemoryFiles(
        [
            new ExportPaths.MemoryInput(Second, "dup", CurrentVersion: 1),
            new ExportPaths.MemoryInput(First, "dup", CurrentVersion: 1),
        ]);

        files[First].ShouldBe("mem-dup.v1.md");
        files[Second].ShouldBe($"mem-dup.v1--{ExportPaths.CollisionSuffix(Second)}.md");
    }

    [Fact]
    public void Memory_file_double_collision_falls_back_to_full_uuid()
    {
        // Second and Third share the same first-8 hex chars, so the short suffix collides too.
        var third = Guid.Parse("22222222-2222-2222-2222-333333333333");
        IReadOnlyDictionary<Guid, string> files = ExportPaths.AssignMemoryFiles(
        [
            new ExportPaths.MemoryInput(First, "dup", CurrentVersion: 1),
            new ExportPaths.MemoryInput(Second, "dup", CurrentVersion: 1),
            new ExportPaths.MemoryInput(third, "dup", CurrentVersion: 1),
        ]);

        files.Values.Distinct(StringComparer.Ordinal).Count().ShouldBe(3);
        files[third].ShouldBe($"mem-dup.v1--{third:N}.md");
    }

    [Fact]
    public void Group_folder_double_collision_falls_back_to_full_uuid()
    {
        var third = Guid.Parse("22222222-2222-2222-2222-333333333333");
        IReadOnlyDictionary<Guid, string> folders = ExportPaths.AssignGroupFolders(
        [
            new ExportPaths.GroupInput(First, "Same Name"),
            new ExportPaths.GroupInput(Second, "Same Name"),
            new ExportPaths.GroupInput(third, "Same Name"),
        ]);

        folders.Values.Distinct(StringComparer.Ordinal).Count().ShouldBe(3);
        folders[third].ShouldBe($"same-name--{third:N}");
    }

    [Fact]
    public void Group_file_is_underscore_group()
    {
        ExportPaths.GroupFile("persistence-layer").ShouldBe("groups/persistence-layer/_group.md");
    }

    [Fact]
    public void Assignment_is_deterministic()
    {
        ExportPaths.GroupInput[] groups =
        [
            new(Second, "Alpha"),
            new(First, "Alpha"),
        ];

        ExportPaths.AssignGroupFolders(groups).ShouldBe(ExportPaths.AssignGroupFolders(groups.Reverse()));
    }
}
