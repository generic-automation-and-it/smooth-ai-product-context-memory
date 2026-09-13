using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// Pins the relationship uniqueness and integrity contract as the graph store provides it.
/// A failure here is a broken invariant, not an obsolete test.
/// </summary>
public sealed class LinkTests : PersistenceTestBase
{
    public LinkTests(AspireFixture aspire) : base(aspire) { }

    private IMemoryGraph Graph => new NpgsqlMemoryGraph(Db);

    [Fact]
    public async Task Link_PersistsWithReason_AndReverseLookupByTarget()
    {
        var (source, target) = await SeedPairAsync();

        (await Graph.CreateAsync(source.Uuid, target.Uuid, MemoryRelation.DependsOn, "The target must hold before the source can hold", Ct))
            .ShouldBeTrue();

        IReadOnlyList<MemoryRelationship> reverse = await Graph.ListTouchingAsync(target.Uuid, Ct);
        MemoryRelationship link = reverse.Single();
        link.SourceUuid.ShouldBe(source.Uuid);
        link.TargetUuid.ShouldBe(target.Uuid);
        link.Relation.ShouldBe(MemoryRelation.DependsOn);
        link.Reason.ShouldBe("The target must hold before the source can hold");
    }

    [Fact]
    public async Task SamePair_CanHoldMultipleRelations()
    {
        var (a, b) = await SeedPairAsync();

        (await Graph.CreateAsync(a.Uuid, b.Uuid, MemoryRelation.DependsOn, "reason 1", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(a.Uuid, b.Uuid, MemoryRelation.RelatesTo, "reason 2", Ct)).ShouldBeTrue();

        IReadOnlyList<MemoryRelationship> stored = await Graph.ListTouchingAsync(a.Uuid, Ct);
        stored.Count(l => l.SourceUuid == a.Uuid && l.TargetUuid == b.Uuid).ShouldBe(2);
    }

    [Fact]
    public async Task SameDirectedRelation_IsRejected()
    {
        var (a, b) = await SeedPairAsync();

        (await Graph.CreateAsync(a.Uuid, b.Uuid, MemoryRelation.DependsOn, "first", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(a.Uuid, b.Uuid, MemoryRelation.DependsOn, "second", Ct)).ShouldBeFalse();

        IReadOnlyList<MemoryRelationship> stored = await Graph.ListTouchingAsync(a.Uuid, Ct);
        stored.Count(l => l.SourceUuid == a.Uuid
            && l.TargetUuid == b.Uuid
            && l.Relation == MemoryRelation.DependsOn).ShouldBe(1);
        stored.Single(l => l.Relation == MemoryRelation.DependsOn).Reason.ShouldBe("first");
    }

    [Fact]
    public async Task OppositeDirections_WithSameRelation_BothPersist()
    {
        var (a, b) = await SeedPairAsync();

        (await Graph.CreateAsync(a.Uuid, b.Uuid, MemoryRelation.RelatesTo, "a to b", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(b.Uuid, a.Uuid, MemoryRelation.RelatesTo, "b to a", Ct)).ShouldBeTrue();

        IReadOnlyList<MemoryRelationship> stored = await Graph.ListAllAsync(Ct);
        stored.Count.ShouldBe(2);
        stored.ShouldContain(l => l.SourceUuid == a.Uuid && l.TargetUuid == b.Uuid
            && l.Relation == MemoryRelation.RelatesTo);
        stored.ShouldContain(l => l.SourceUuid == b.Uuid && l.TargetUuid == a.Uuid
            && l.Relation == MemoryRelation.RelatesTo);
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

        Guid deletedUuid = a.Uuid;
        Guid unrelatedSource = c.Uuid;
        Guid unrelatedTarget = d.Uuid;

        (await Graph.CreateAsync(a.Uuid, b.Uuid, MemoryRelation.DependsOn, "outbound from deleted", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(c.Uuid, a.Uuid, MemoryRelation.RelatesTo, "inbound to deleted", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(c.Uuid, d.Uuid, MemoryRelation.Implements, "unrelated pair", Ct)).ShouldBeTrue();

        Db.Memories.Remove(a);
        await Db.SaveChangesAsync(Ct);

        (await Graph.ListTouchingAsync(deletedUuid, Ct)).ShouldBeEmpty();
        IReadOnlyList<MemoryRelationship> unrelated = await Graph.ListTouchingAsync(unrelatedSource, Ct);
        unrelated.Count(l => l.SourceUuid == unrelatedSource && l.TargetUuid == unrelatedTarget).ShouldBe(1);
    }

    [Fact]
    public async Task GroupDelete_RemovesEdgesForCascadedMemories()
    {
        var group = TestEntities.NewGroup();
        var other = TestEntities.NewGroup();
        Db.MemoryGroups.AddRange(group, other);
        await Db.SaveChangesAsync(Ct);

        var a = TestEntities.NewMemory(group.Id, "A", "Subject A");
        var b = TestEntities.NewMemory(group.Id, "B", "Subject B");
        var c = TestEntities.NewMemory(other.Id, "C", "Subject C");
        Db.Memories.AddRange(a, b, c);
        await Db.SaveChangesAsync(Ct);

        Guid deletedA = a.Uuid;
        Guid deletedB = b.Uuid;

        (await Graph.CreateAsync(a.Uuid, b.Uuid, MemoryRelation.DependsOn, "inside group", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(c.Uuid, a.Uuid, MemoryRelation.RelatesTo, "into group", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(c.Uuid, c.Uuid, MemoryRelation.RelatesTo, "unrelated loop", Ct)).ShouldBeTrue();

        await using var conn = await DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await using (var set = new NpgsqlCommand("SET LOCAL app.allow_history_delete = 'true'", conn, tx))
        {
            await set.ExecuteNonQueryAsync(Ct);
        }

        await using (var del = new NpgsqlCommand("DELETE FROM memory_group WHERE id = @id", conn, tx))
        {
            del.Parameters.AddWithValue("id", group.Id);
            await del.ExecuteNonQueryAsync(Ct);
        }

        await tx.CommitAsync(Ct);

        (await Graph.ListTouchingAsync(deletedA, Ct)).ShouldBeEmpty();
        (await Graph.ListTouchingAsync(deletedB, Ct)).ShouldBeEmpty();
        (await Graph.ListTouchingAsync(c.Uuid, Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ForcedFailureBetweenEdgeRemovalAndMemoryDelete_LeavesBothPresent()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var a = TestEntities.NewMemory(group.Id, "A", "Subject A");
        var b = TestEntities.NewMemory(group.Id, "B", "Subject B");
        Db.Memories.AddRange(a, b);
        await Db.SaveChangesAsync(Ct);
        Db.MemoryVersions.Add(TestEntities.NewVersion(a.Id, 1, "Claim"));
        await Db.SaveChangesAsync(Ct);

        (await Graph.CreateAsync(a.Uuid, b.Uuid, MemoryRelation.DependsOn, "must survive rollback", Ct)).ShouldBeTrue();

        await Should.ThrowAsync<DbUpdateException>(() =>
        {
            Db.Memories.Remove(a);
            return Db.SaveChangesAsync(Ct);
        });

        (await Db.Memories.AsNoTracking().CountAsync(m => m.Uuid == a.Uuid, Ct)).ShouldBe(1);
        IReadOnlyList<MemoryRelationship> remaining = await Graph.ListTouchingAsync(a.Uuid, Ct);
        remaining.Count.ShouldBe(1);
        remaining[0].Reason.ShouldBe("must survive rollback");
    }

    [Fact]
    public async Task Vertex_CarriesIdentityOnly()
    {
        var (a, b) = await SeedPairAsync();
        (await Graph.CreateAsync(a.Uuid, b.Uuid, MemoryRelation.RelatesTo, "why", Ct)).ShouldBeTrue();

        await using var conn = await DataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT keys::text FROM ag_catalog.cypher('{AgeSession.GraphName}', $$
                MATCH (v:Memory)
                RETURN keys(v)
            $$) AS (keys agtype);
            """,
            conn);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            string keys = reader.GetString(0);
            keys.ShouldContain("memory_uuid");
            keys.ShouldNotContain("subject");
            keys.ShouldNotContain("description");
            keys.ShouldNotContain("kind");
            keys.ShouldNotContain("scope");
            keys.ShouldNotContain("claim");
            keys.ShouldNotContain("reason");
            keys.ShouldBe("[\"memory_uuid\"]");
        }
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

        (await Graph.CreateAsync(memory.Uuid, memory.Uuid, MemoryRelation.RelatesTo, "store has no self-link prevention", Ct))
            .ShouldBeTrue();

        MemoryRelationship stored = (await Graph.ListTouchingAsync(memory.Uuid, Ct)).Single();
        stored.SourceUuid.ShouldBe(memory.Uuid);
        stored.TargetUuid.ShouldBe(memory.Uuid);
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

        (await Graph.CreateAsync(source.Uuid, target.Uuid, MemoryRelation.DependsOn, "relationships are not group-bounded", Ct))
            .ShouldBeTrue();

        MemoryRelationship stored = (await Graph.ListAllAsync(Ct)).Single();
        stored.SourceUuid.ShouldBe(source.Uuid);
        stored.TargetUuid.ShouldBe(target.Uuid);
        (await Db.Memories.AsNoTracking().SingleAsync(m => m.Uuid == source.Uuid, Ct)).GroupId
            .ShouldNotBe((await Db.Memories.AsNoTracking().SingleAsync(m => m.Uuid == target.Uuid, Ct)).GroupId);
    }

    [Fact]
    public async Task UnknownRelation_Persists()
    {
        var (a, b) = await SeedPairAsync();

        (await Graph.CreateAsync(a.Uuid, b.Uuid, "derived_from", "open vocabulary", Ct)).ShouldBeTrue();

        MemoryRelationship stored = (await Graph.ListAllAsync(Ct)).Single();
        stored.Relation.ShouldBe("derived_from");
        stored.Reason.ShouldBe("open vocabulary");
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
