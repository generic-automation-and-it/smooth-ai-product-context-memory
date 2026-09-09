using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

public sealed class CloneAndSlugTests : PersistenceTestBase
{
    public CloneAndSlugTests(AspireFixture aspire) : base(aspire) { }

    [Fact]
    public async Task Clone_SharesLineageAcrossGroups_WithDistinctUuids()
    {
        var groupA = TestEntities.NewGroup(repo: "acme/api");
        var groupB = TestEntities.NewGroup(repo: "acme/web");
        Db.MemoryGroups.AddRange(groupA, groupB);
        await Db.SaveChangesAsync(Ct);

        Guid lineage = Guid.NewGuid();
        var memoryA = new Memory
        {
            Uuid = Guid.NewGuid(),
            LineageId = lineage,
            GroupId = groupA.Id,
            Name = "Same fact",
            Description = "The same subject",
            SubjectSlug = "the-same-subject",
            Tags = [],
            Facets = [],
        };
        var memoryB = new Memory
        {
            Uuid = Guid.NewGuid(),
            LineageId = lineage,
            GroupId = groupB.Id,
            Name = "Same fact",
            Description = "The same subject",
            SubjectSlug = "the-same-subject",
            Tags = [],
            Facets = [],
        };

        Db.Memories.AddRange(memoryA, memoryB);
        await Db.SaveChangesAsync(Ct);

        Db.Memories.AsNoTracking().Single(m => m.Id == memoryA.Id).Uuid.ShouldNotBe(memoryB.Uuid);
        Db.Memories.AsNoTracking().Single(m => m.Id == memoryB.Id).LineageId.ShouldBe(lineage);
    }

    [Fact]
    public async Task SubjectSlug_IsUniqueWithinGroup()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        Db.Memories.Add(TestEntities.NewMemory(group.Id, "A", "Duplicate subject", subjectSlug: "dup"));
        Db.Memories.Add(TestEntities.NewMemory(group.Id, "B", "Another dup", subjectSlug: "dup"));

        // The soft unique (group_id, subject_slug) is an exact-match backstop, so a duplicate slug
        // within one group raises even though the constraint is intentionally not semantically deep.
        await Should.ThrowAsync<DbUpdateException>(() => Db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task SubjectSlug_DoesNotCollideAcrossGroups()
    {
        var groupA = TestEntities.NewGroup();
        var groupB = TestEntities.NewGroup();
        Db.MemoryGroups.AddRange(groupA, groupB);
        await Db.SaveChangesAsync(Ct);

        Db.Memories.Add(TestEntities.NewMemory(groupA.Id, "A", "Same subject", subjectSlug: "same"));
        Db.Memories.Add(TestEntities.NewMemory(groupB.Id, "B", "Same subject", subjectSlug: "same"));
        await Db.SaveChangesAsync(Ct);

        (await Db.Memories.CountAsync(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Memory_DefaultSlug_MatchesDescription()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        Memory memory = TestEntities.NewMemory(group.Id, "A", "PostgreSQL persistence layer");
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);

        Db.Memories.AsNoTracking().Single(m => m.Id == memory.Id).SubjectSlug.ShouldBe("postgresql-persistence-layer");
    }
}
