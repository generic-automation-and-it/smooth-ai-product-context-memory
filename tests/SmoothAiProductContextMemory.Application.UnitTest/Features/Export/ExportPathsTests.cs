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
        // check after the copy can see.
        //
        // Two descriptions that fold together are one slug, so this is a genuine collision and the uuid
        // suffix must separate them rather than both claiming one filename.
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

        files[First].ShouldNotBe(files[Second]);
    }

    [Fact]
    public void Unslugged_subjects_differing_only_by_case_still_get_distinct_files()
    {
        // The backstop, and the half of the safety the test above cannot reach.
        //
        // `MemoryInput.SubjectSlug` is an unvalidated `string` that `ExportStore` forwards verbatim from
        // the stored column, and the test above routes its own input through `Slug.Subject` — so it can
        // only ever prove the slug property, never the boundary. An untrusted archive restore or a direct
        // database write carrying an unslugged value would hand this method two names that differ only
        // by case, and the collision suffix is what keeps them apart.
        //
        // This is the case where the safety stops depending on a single producer: the names arrive
        // already wrong, and the suffix still separates them.
        IReadOnlyDictionary<Guid, string> files = ExportPaths.AssignMemoryFiles(
        [
            new ExportPaths.MemoryInput(First, "STORAGE-engine", CurrentVersion: 1),
            new ExportPaths.MemoryInput(Second, "Storage-Engine", CurrentVersion: 1),
        ]);

        files[First].ShouldNotBe(files[Second]);
        // Distinct under the comparison that matters: the filesystem does not care which case it got.
        new HashSet<string>(files.Values, StringComparer.OrdinalIgnoreCase)
            .Count.ShouldBe(2, "two export files must remain distinct under case-insensitive comparison");
    }

    [Fact]
    public void Group_folders_differing_only_by_case_stay_distinct()
    {
        // Pins the property at the boundary. It does **not** discriminate the comparer: mutating the
        // `used` set back to `Ordinal` leaves this green, because `GroupSlug` routes both names through
        // `Slug.TrySubject`, which folds them to one name before the set ever sees them — so the
        // collision is found ordinally and the suffix applies either way. There is no input that reaches
        // `AssignGroupFolders` with case-differing names today, which is why the guard is defence in
        // depth rather than a fix, and why this test is a pin and not evidence.
        IReadOnlyDictionary<Guid, string> folders = ExportPaths.AssignGroupFolders(
        [
            new ExportPaths.GroupInput(First, "Storage-Engine"),
            new ExportPaths.GroupInput(Second, "STORAGE-engine"),
        ]);

        new HashSet<string>(folders.Values, StringComparer.OrdinalIgnoreCase)
            .Count.ShouldBe(2, "two group folders must remain distinct under case-insensitive comparison");
    }

    [Fact]
    public void Slugging_is_what_makes_case_collision_impossible()
    {
        // The property the whole export path rests on: `Slug.Subject` is what lower-cases, so two
        // descriptions that differ only by case produce one subject and reach the collision suffix as a
        // genuine collision. Without it the file names would differ by case only, which is one file on a
        // case-insensitive volume.
        //
        // This duplicates `Domain.UnitTest/SlugTests.cs`, and that is deliberate rather than accidental
        // drift: the domain test proves the slug function is correct, this one proves the export's safety
        // depends on that function. The mutation that breaks the guarantee fails here too, which is what
        // makes it a guard on the export rather than a restatement of the slug.
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
