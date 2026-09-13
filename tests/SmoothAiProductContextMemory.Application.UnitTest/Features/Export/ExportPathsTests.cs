using SmoothAiProductContextMemory.Application.Features.Export;

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
    public void Memory_files_use_mem_prefix()
    {
        IReadOnlyDictionary<Guid, string> files = ExportPaths.AssignMemoryFiles(
        [
            new ExportPaths.MemoryInput(First, "postgres-storage"),
        ]);

        files[First].ShouldBe("mem-postgres-storage.md");
        ExportPaths.MemoryFile("persistence-layer", files[First])
            .ShouldBe("groups/persistence-layer/mem-postgres-storage.md");
    }

    [Fact]
    public void Memory_file_collision_suffixes_later_uuid()
    {
        IReadOnlyDictionary<Guid, string> files = ExportPaths.AssignMemoryFiles(
        [
            new ExportPaths.MemoryInput(Second, "dup"),
            new ExportPaths.MemoryInput(First, "dup"),
        ]);

        files[First].ShouldBe("mem-dup.md");
        files[Second].ShouldBe($"mem-dup--{ExportPaths.CollisionSuffix(Second)}.md");
    }

    [Fact]
    public void Memory_file_double_collision_falls_back_to_full_uuid()
    {
        // Second and Third share the same first-8 hex chars, so the short suffix collides too.
        var third = Guid.Parse("22222222-2222-2222-2222-333333333333");
        IReadOnlyDictionary<Guid, string> files = ExportPaths.AssignMemoryFiles(
        [
            new ExportPaths.MemoryInput(First, "dup"),
            new ExportPaths.MemoryInput(Second, "dup"),
            new ExportPaths.MemoryInput(third, "dup"),
        ]);

        files.Values.Distinct(StringComparer.Ordinal).Count().ShouldBe(3);
        files[third].ShouldBe($"mem-dup--{third:N}.md");
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
