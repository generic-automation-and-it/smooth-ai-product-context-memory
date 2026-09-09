using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

public sealed class LinkTests : PersistenceTestBase
{
    public LinkTests(AspireFixture aspire) : base(aspire) { }

    [Fact]
    public async Task Link_PersistsWithReason_AndReverseLookupByTargetIsIndexServed()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var source = TestEntities.NewMemory(group.Id, "Source", "The source subject");
        var target = TestEntities.NewMemory(group.Id, "Target", "The target subject");
        Db.Memories.AddRange(source, target);
        await Db.SaveChangesAsync(Ct);

        var link = new MemoryLink
        {
            SourceMemoryId = source.Id,
            TargetMemoryId = target.Id,
            Relation = MemoryLink.RelationValue.DependsOn,
            Reason = "The target must hold before the source can hold",
        };
        Db.MemoryLinks.Add(link);
        await Db.SaveChangesAsync(Ct);

        // Forward: source -> outbound links.
        var forward = await Db.MemoryLinks.AsNoTracking()
            .Where(l => l.SourceMemoryId == source.Id)
            .SingleAsync(Ct);
        forward.Reason.ShouldBe("The target must hold before the source can hold");
        forward.Relation.ShouldBe(MemoryLink.RelationValue.DependsOn);

        // Reverse: what points at this memory — index-served on target_memory_id.
        var reverse = await Db.MemoryLinks.AsNoTracking()
            .Where(l => l.TargetMemoryId == target.Id)
            .ToListAsync(Ct);
        reverse.Single().SourceMemoryId.ShouldBe(source.Id);
    }

    [Fact]
    public async Task SamePair_CanHoldMultipleRelations()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var a = TestEntities.NewMemory(group.Id, "A", "Subject A");
        var b = TestEntities.NewMemory(group.Id, "B", "Subject B");
        Db.Memories.AddRange(a, b);
        await Db.SaveChangesAsync(Ct);

        Db.MemoryLinks.AddRange(
            new MemoryLink { SourceMemoryId = a.Id, TargetMemoryId = b.Id, Relation = MemoryLink.RelationValue.DependsOn, Reason = "reason 1" },
            new MemoryLink { SourceMemoryId = a.Id, TargetMemoryId = b.Id, Relation = MemoryLink.RelationValue.RelatesTo, Reason = "reason 2" });
        await Db.SaveChangesAsync(Ct);

        (await Db.MemoryLinks.CountAsync(l => l.SourceMemoryId == a.Id && l.TargetMemoryId == b.Id, Ct)).ShouldBe(2);
    }
}
