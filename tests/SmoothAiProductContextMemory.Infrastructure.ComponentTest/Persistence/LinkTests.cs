using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// Pins the relationship uniqueness and integrity contract as the store provides it today.
/// The contract survives a later storage change: a failure here is a broken invariant, not
/// an obsolete test.
/// </summary>
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

    [Fact]
    public async Task SameDirectedRelation_IsRejected()
    {
        var (a, b) = await SeedPairAsync();

        Db.MemoryLinks.Add(new MemoryLink
        {
            SourceMemoryId = a.Id,
            TargetMemoryId = b.Id,
            Relation = MemoryLink.RelationValue.DependsOn,
            Reason = "first",
        });
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        Db.MemoryLinks.Add(new MemoryLink
        {
            SourceMemoryId = a.Id,
            TargetMemoryId = b.Id,
            Relation = MemoryLink.RelationValue.DependsOn,
            Reason = "second",
        });

        // The exception-shape assertions below attribute the refusal to the uniqueness
        // constraint as the relational store raises it today. After the WT-02 cutover the
        // invariant moves into the application, so the *behaviour* (second triple refused,
        // count stays 1) must hold while these three lines are edited to match the new
        // failure shape — that edit is expected, not a sign the test is obsolete.
        var ex = await Should.ThrowAsync<DbUpdateException>(() => Db.SaveChangesAsync(Ct));
        PostgresException postgres = ex.InnerException.ShouldBeOfType<PostgresException>();
        postgres.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        postgres.ConstraintName.ShouldBe("PK_memory_link");

        (await Db.MemoryLinks.AsNoTracking()
            .CountAsync(l => l.SourceMemoryId == a.Id
                && l.TargetMemoryId == b.Id
                && l.Relation == MemoryLink.RelationValue.DependsOn, Ct))
            .ShouldBe(1);
    }

    [Fact]
    public async Task OppositeDirections_WithSameRelation_BothPersist()
    {
        var (a, b) = await SeedPairAsync();

        Db.MemoryLinks.AddRange(
            new MemoryLink
            {
                SourceMemoryId = a.Id,
                TargetMemoryId = b.Id,
                Relation = MemoryLink.RelationValue.RelatesTo,
                Reason = "a to b",
            },
            new MemoryLink
            {
                SourceMemoryId = b.Id,
                TargetMemoryId = a.Id,
                Relation = MemoryLink.RelationValue.RelatesTo,
                Reason = "b to a",
            });
        await Db.SaveChangesAsync(Ct);

        var stored = await Db.MemoryLinks.AsNoTracking().ToListAsync(Ct);
        stored.Count.ShouldBe(2);
        stored.ShouldContain(l => l.SourceMemoryId == a.Id && l.TargetMemoryId == b.Id
            && l.Relation == MemoryLink.RelationValue.RelatesTo);
        stored.ShouldContain(l => l.SourceMemoryId == b.Id && l.TargetMemoryId == a.Id
            && l.Relation == MemoryLink.RelationValue.RelatesTo);
    }

    [Fact]
    public async Task DeletingMemory_RemovesInboundAndOutboundLinks_UnrelatedLinksSurvive()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var a = TestEntities.NewMemory(group.Id, "A", "Subject A");
        var b = TestEntities.NewMemory(group.Id, "B", "Subject B");
        var c = TestEntities.NewMemory(group.Id, "C", "Subject C");
        var d = TestEntities.NewMemory(group.Id, "D", "Subject D");
        Db.Memories.AddRange(a, b, c, d);
        await Db.SaveChangesAsync(Ct);

        long deletedId = a.Id;
        long unrelatedSourceId = c.Id;
        long unrelatedTargetId = d.Id;

        Db.MemoryLinks.AddRange(
            new MemoryLink
            {
                SourceMemoryId = a.Id,
                TargetMemoryId = b.Id,
                Relation = MemoryLink.RelationValue.DependsOn,
                Reason = "outbound from deleted",
            },
            new MemoryLink
            {
                SourceMemoryId = c.Id,
                TargetMemoryId = a.Id,
                Relation = MemoryLink.RelationValue.RelatesTo,
                Reason = "inbound to deleted",
            },
            new MemoryLink
            {
                SourceMemoryId = c.Id,
                TargetMemoryId = d.Id,
                Relation = MemoryLink.RelationValue.Implements,
                Reason = "unrelated pair",
            });
        await Db.SaveChangesAsync(Ct);

        Db.Memories.Remove(a);
        await Db.SaveChangesAsync(Ct);

        (await Db.MemoryLinks.AsNoTracking()
            .CountAsync(l => l.SourceMemoryId == deletedId || l.TargetMemoryId == deletedId, Ct))
            .ShouldBe(0);
        (await Db.MemoryLinks.AsNoTracking()
            .CountAsync(l => l.SourceMemoryId == unrelatedSourceId && l.TargetMemoryId == unrelatedTargetId, Ct))
            .ShouldBe(1);
    }

    [Fact]
    public async Task SelfLink_PersistsAtStore()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var memory = TestEntities.NewMemory(group.Id, "Loop", "Self link subject");
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);

        Db.MemoryLinks.Add(new MemoryLink
        {
            SourceMemoryId = memory.Id,
            TargetMemoryId = memory.Id,
            Relation = MemoryLink.RelationValue.RelatesTo,
            Reason = "store has no self-link prevention",
        });
        await Db.SaveChangesAsync(Ct);

        MemoryLink stored = await Db.MemoryLinks.AsNoTracking().SingleAsync(Ct);
        stored.SourceMemoryId.ShouldBe(memory.Id);
        stored.TargetMemoryId.ShouldBe(memory.Id);
    }

    [Fact]
    public async Task Link_PersistsAcrossGroups()
    {
        var sourceGroup = TestEntities.NewGroup();
        var targetGroup = TestEntities.NewGroup();
        Db.MemoryGroups.AddRange(sourceGroup, targetGroup);
        await Db.SaveChangesAsync(Ct);

        var source = TestEntities.NewMemory(sourceGroup.Id, "Source", "Source in group one");
        var target = TestEntities.NewMemory(targetGroup.Id, "Target", "Target in group two");
        Db.Memories.AddRange(source, target);
        await Db.SaveChangesAsync(Ct);

        Db.MemoryLinks.Add(new MemoryLink
        {
            SourceMemoryId = source.Id,
            TargetMemoryId = target.Id,
            Relation = MemoryLink.RelationValue.DependsOn,
            Reason = "relationships are not group-bounded",
        });
        await Db.SaveChangesAsync(Ct);

        MemoryLink stored = await Db.MemoryLinks.AsNoTracking().SingleAsync(Ct);
        stored.SourceMemoryId.ShouldBe(source.Id);
        stored.TargetMemoryId.ShouldBe(target.Id);
        (await Db.Memories.AsNoTracking().SingleAsync(m => m.Id == source.Id, Ct)).GroupId
            .ShouldNotBe((await Db.Memories.AsNoTracking().SingleAsync(m => m.Id == target.Id, Ct)).GroupId);
    }

    private async Task<(Memory A, Memory B)> SeedPairAsync()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var a = TestEntities.NewMemory(group.Id, "A", "Subject A");
        var b = TestEntities.NewMemory(group.Id, "B", "Subject B");
        Db.Memories.AddRange(a, b);
        await Db.SaveChangesAsync(Ct);
        return (a, b);
    }
}
