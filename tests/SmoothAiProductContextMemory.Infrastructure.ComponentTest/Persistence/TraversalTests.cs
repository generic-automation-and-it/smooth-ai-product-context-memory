using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// Pins bounded traversal — the capability HLD-003 exists for (Goal 1). A failure here is a lost
/// capability, not an obsolete test.
/// </summary>
public sealed class TraversalTests : PersistenceTestBase
{
    public TraversalTests(AspireFixture aspire) : base(aspire) { }

    private IMemoryGraph Graph => new NpgsqlMemoryGraph(Db);

    private IMemoryTraversal Traversal => new NpgsqlMemoryTraversal(Db);

    [Fact]
    public async Task DepthThreePath_ReturnsIntermediateHopsInOrder_WithRelationTypes()
    {
        Chain chain = await SeedChainAsync();

        IReadOnlyList<MemoryPath> paths = await Traversal.FindPathsAsync(
            new MemoryPathQuery { SourceUuid = chain.A, TargetUuid = chain.D, MaxDepth = 3 },
            Ct);

        MemoryPath path = paths.Single();
        path.Depth.ShouldBe(3);
        path.Hops.Select(h => h.SourceUuid).ShouldBe([chain.A, chain.B, chain.C]);
        path.Hops.Select(h => h.TargetUuid).ShouldBe([chain.B, chain.C, chain.D]);
        path.Hops.Select(h => h.Relation).ShouldBe(
            [MemoryRelation.DependsOn, MemoryRelation.DependsOn, MemoryRelation.RelatesTo]);
        path.Hops.Select(h => h.Reason).ShouldBe(["a to b", "b to c", "c to d"]);
    }

    [Fact]
    public async Task Traversal_IsFilterableByRelationType()
    {
        Chain chain = await SeedChainAsync();

        IReadOnlyList<MemoryPath> paths = await Traversal.FindPathsAsync(
            new MemoryPathQuery
            {
                SourceUuid = chain.A,
                MaxDepth = 3,
                Relation = MemoryRelation.DependsOn,
            },
            Ct);

        // The relates_to hop is not traversed, so D is unreachable and the chain stops at C.
        paths.Select(p => p.Endpoint.Uuid).Order().ShouldBe(new[] { chain.B, chain.C }.Order());
        paths.SelectMany(p => p.Hops).ShouldAllBe(h => h.Relation == MemoryRelation.DependsOn);
    }

    [Fact]
    public async Task Traversal_IsFilterableByDirection()
    {
        Chain chain = await SeedChainAsync();

        IReadOnlyList<MemoryPath> outbound = await Traversal.FindPathsAsync(
            new MemoryPathQuery { SourceUuid = chain.A, MaxDepth = 3 },
            Ct);
        IReadOnlyList<MemoryPath> inbound = await Traversal.FindPathsAsync(
            new MemoryPathQuery
            {
                SourceUuid = chain.D,
                MaxDepth = 3,
                Direction = TraversalDirection.Inbound,
            },
            Ct);

        outbound.Select(p => p.Endpoint.Uuid).Order().ShouldBe(new[] { chain.B, chain.C, chain.D }.Order());
        inbound.Select(p => p.Endpoint.Uuid).Order().ShouldBe(new[] { chain.A, chain.B, chain.C }.Order());

        // Direction is part of a relationship's identity, so an inbound walk still reports the edge
        // the way it was written.
        inbound.SelectMany(p => p.Hops).ShouldContain(h => h.SourceUuid == chain.A && h.TargetUuid == chain.B);
        inbound.SelectMany(p => p.Hops).ShouldNotContain(h => h.SourceUuid == chain.B && h.TargetUuid == chain.A);
    }

    [Fact]
    public async Task EitherDirection_ReachesWhatNeitherSingleDirectionDoes()
    {
        MemoryGroup group = await SeedGroupAsync();
        Memory hub = await SeedMemoryAsync(group.Id, "Hub", "Hub subject");
        Memory left = await SeedMemoryAsync(group.Id, "Left", "Left subject");
        Memory right = await SeedMemoryAsync(group.Id, "Right", "Right subject");

        // left -> hub <- right: nothing is reachable from left in one direction alone.
        (await Graph.CreateAsync(left.Uuid, hub.Uuid, MemoryRelation.DependsOn, "left to hub", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(right.Uuid, hub.Uuid, MemoryRelation.DependsOn, "right to hub", Ct)).ShouldBeTrue();

        IReadOnlyList<MemoryPath> outbound = await Traversal.FindPathsAsync(
            new MemoryPathQuery { SourceUuid = left.Uuid, TargetUuid = right.Uuid, MaxDepth = 2 },
            Ct);
        IReadOnlyList<MemoryPath> either = await Traversal.FindPathsAsync(
            new MemoryPathQuery
            {
                SourceUuid = left.Uuid,
                TargetUuid = right.Uuid,
                MaxDepth = 2,
                Direction = TraversalDirection.Either,
            },
            Ct);

        outbound.ShouldBeEmpty();
        either.Single().Hops.Select(h => h.TargetUuid).ShouldAllBe(uuid => uuid == hub.Uuid);
    }

    [Fact]
    public async Task ComposedQuery_ReturnsDescriptiveFieldsFromTheRelationalRow()
    {
        Chain chain = await SeedChainAsync();

        MemoryPath path = (await Traversal.FindPathsAsync(
            new MemoryPathQuery { SourceUuid = chain.A, TargetUuid = chain.D, MaxDepth = 3 },
            Ct)).Single();

        // The graph holds identity only; every descriptive field here came from memory / memory_version
        // in the same statement.
        path.Endpoint.Uuid.ShouldBe(chain.D);
        path.Endpoint.Name.ShouldBe("D");
        path.Endpoint.Description.ShouldBe("Subject D");
        path.Endpoint.Statement.ShouldBe("Claim D");
        path.Endpoint.Kind.ShouldBe(MemoryVersion.KindValue.Decision);
        path.Endpoint.Status.ShouldBe(MemoryVersion.MemoryVersionStatus.Approved);
        path.Endpoint.IsCurrent.ShouldBeTrue();
        path.Endpoint.Version.ShouldBe(1);
        path.Endpoint.GroupUuid.ShouldBe(chain.GroupUuid);
        path.Endpoint.ScopeDimension.ShouldBe(MemoryGroup.ScopeDimensionValue.Product);
    }

    [Fact]
    public async Task ComposedQuery_NarrowsByRelationalPredicate()
    {
        Chain chain = await SeedChainAsync();

        IReadOnlyList<MemoryPath> decisions = await Traversal.FindPathsAsync(
            new MemoryPathQuery
            {
                SourceUuid = chain.A,
                MaxDepth = 3,
                Kind = MemoryVersion.KindValue.Decision,
            },
            Ct);

        // B and C are measurements; only D is the decision they justified.
        decisions.Select(p => p.Endpoint.Uuid).ShouldBe([chain.D]);
    }

    [Fact]
    public async Task ScopeRule_HidesProgrammeScopedEndpoints()
    {
        MemoryGroup product = await SeedGroupAsync();
        MemoryGroup programme = await SeedGroupAsync(MemoryGroup.ScopeDimensionValue.Program);
        Memory source = await SeedMemoryAsync(product.Id, "Source", "Source subject");
        Memory hidden = await SeedMemoryAsync(programme.Id, "Programme", "Programme subject");

        (await Graph.CreateAsync(source.Uuid, hidden.Uuid, MemoryRelation.RelatesTo, "into programme", Ct))
            .ShouldBeTrue();

        IReadOnlyList<MemoryPath> open = await Traversal.FindPathsAsync(
            new MemoryPathQuery
            {
                SourceUuid = source.Uuid,
                MaxDepth = 2,
                ExcludedScopeDimensions = [MemoryGroup.ScopeDimensionValue.Program],
            },
            Ct);
        IReadOnlyList<MemoryPath> declared = await Traversal.FindPathsAsync(
            new MemoryPathQuery
            {
                SourceUuid = source.Uuid,
                MaxDepth = 2,
                RequiredScopeDimension = MemoryGroup.ScopeDimensionValue.Program,
            },
            Ct);

        open.ShouldBeEmpty();
        declared.Single().Endpoint.Uuid.ShouldBe(hidden.Uuid);
    }

    [Fact]
    public async Task ScopeRule_HidesPathsRoutedThroughAnExcludedIntermediate()
    {
        MemoryGroup product = await SeedGroupAsync();
        MemoryGroup programme = await SeedGroupAsync(MemoryGroup.ScopeDimensionValue.Program);
        Memory source = await SeedMemoryAsync(product.Id, "Source", "Source subject");
        Memory hidden = await SeedMemoryAsync(programme.Id, "Programme", "Programme subject");
        Memory endpoint = await SeedMemoryAsync(product.Id, "Endpoint", "Endpoint subject");

        // Both endpoints are product-scoped; only the middle hop is programme-scoped.
        (await Graph.CreateAsync(source.Uuid, hidden.Uuid, MemoryRelation.DependsOn, "programme rationale", Ct))
            .ShouldBeTrue();
        (await Graph.CreateAsync(hidden.Uuid, endpoint.Uuid, MemoryRelation.DependsOn, "leads to the decision", Ct))
            .ShouldBeTrue();

        IReadOnlyList<MemoryPath> open = await Traversal.FindPathsAsync(
            new MemoryPathQuery
            {
                SourceUuid = source.Uuid,
                MaxDepth = 3,
                ExcludedScopeDimensions = [MemoryGroup.ScopeDimensionValue.Program],
            },
            Ct);

        // Filtering only the endpoint would return this path and disclose the programme memory's
        // identity and its edge reasons — descriptive content, on a read path, with no scope declared.
        open.SelectMany(p => p.Hops)
            .ShouldNotContain(h => h.SourceUuid == hidden.Uuid || h.TargetUuid == hidden.Uuid);
        open.Select(p => p.Endpoint.Uuid).ShouldNotContain(endpoint.Uuid);
    }

    [Fact]
    public async Task CyclicGraph_TerminatesAtTheBound()
    {
        MemoryGroup group = await SeedGroupAsync();
        Memory a = await SeedMemoryAsync(group.Id, "A", "Subject A");
        Memory b = await SeedMemoryAsync(group.Id, "B", "Subject B");

        (await Graph.CreateAsync(a.Uuid, b.Uuid, MemoryRelation.RelatesTo, "a to b", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(b.Uuid, a.Uuid, MemoryRelation.RelatesTo, "b to a", Ct)).ShouldBeTrue();

        IReadOnlyList<MemoryPath> paths = await Traversal.FindPathsAsync(
            new MemoryPathQuery { SourceUuid = a.Uuid, MaxDepth = 3 },
            Ct);

        paths.ShouldNotBeEmpty();
        paths.ShouldAllBe(p => p.Depth <= 3);
    }

    [Fact]
    public async Task SelfLink_TerminatesAtTheBound()
    {
        MemoryGroup group = await SeedGroupAsync();
        Memory loop = await SeedMemoryAsync(group.Id, "Loop", "Self link subject");

        // A one-vertex cycle is the tightest loop the store accepts, so it is the shape most likely to
        // expose a bound that is applied to distinct vertices rather than to hops.
        (await Graph.CreateAsync(loop.Uuid, loop.Uuid, MemoryRelation.RelatesTo, "store permits a self link", Ct))
            .ShouldBeTrue();

        IReadOnlyList<MemoryPath> paths = await Traversal.FindPathsAsync(
            new MemoryPathQuery { SourceUuid = loop.Uuid, MaxDepth = 3 },
            Ct);

        paths.ShouldNotBeEmpty();
        paths.ShouldAllBe(p => p.Depth >= 1 && p.Depth <= 3);
        paths.SelectMany(p => p.Hops).ShouldAllBe(h => h.SourceUuid == loop.Uuid && h.TargetUuid == loop.Uuid);
    }

    [Theory]
    [InlineData("}::edge, {\"id\": 1}")]
    [InlineData("contains ::vertex annotation")]
    [InlineData("@kind and @status and $1 look like parameters")]
    [InlineData("$q$ dollar tag $q$")]
    [InlineData("quote ' backslash \\ newline")]
    public async Task AdversarialReason_RoundTripsThroughTheComposedQuery(string reason)
    {
        MemoryGroup group = await SeedGroupAsync();
        Memory a = await SeedMemoryAsync(group.Id, "A", "Subject A");
        Memory b = await SeedMemoryAsync(group.Id, "B", "Subject B");

        (await Graph.CreateAsync(a.Uuid, b.Uuid, MemoryRelation.RelatesTo, reason, Ct)).ShouldBeTrue();

        MemoryPath path = (await Traversal.FindPathsAsync(
            new MemoryPathQuery { SourceUuid = a.Uuid, TargetUuid = b.Uuid, MaxDepth = 1 },
            Ct)).Single();

        path.Hops.Single().Reason.ShouldBe(reason);
    }

    [Fact]
    public async Task DepthOneInbound_AgreesWithTheOneHopReverseLookup()
    {
        Chain chain = await SeedChainAsync();

        IReadOnlyList<MemoryPath> inbound = await Traversal.FindPathsAsync(
            new MemoryPathQuery
            {
                SourceUuid = chain.D,
                MaxDepth = 1,
                Direction = TraversalDirection.Inbound,
            },
            Ct);
        IReadOnlyList<MemoryRelationship> touching = await Graph.ListTouchingAsync(chain.D, Ct);

        // The pre-cutover access pattern and the depth-1 traversal must agree, or one of them is wrong.
        inbound.SelectMany(p => p.Hops).Select(h => (h.SourceUuid, h.TargetUuid, h.Relation))
            .ShouldBe(touching.Select(l => (l.SourceUuid, l.TargetUuid, l.Relation)));
    }

    [Fact]
    public async Task Traversal_RefusesAnUnboundedDepth()
    {
        Chain chain = await SeedChainAsync();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Traversal.FindPathsAsync(
            new MemoryPathQuery { SourceUuid = chain.A, MaxDepth = 0 },
            Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Traversal.FindPathsAsync(
            new MemoryPathQuery
            {
                SourceUuid = chain.A,
                MaxDepth = MemoryTraversalDefaults.MaxDepth + 1,
            },
            Ct));
    }

    private sealed record Chain(Guid GroupUuid, Guid A, Guid B, Guid C, Guid D);

    private async Task<Chain> SeedChainAsync()
    {
        MemoryGroup group = await SeedGroupAsync();
        Memory a = await SeedMemoryAsync(group.Id, "A", "Subject A", "Claim A", MemoryVersion.KindValue.Reference);
        Memory b = await SeedMemoryAsync(group.Id, "B", "Subject B", "Claim B", MemoryVersion.KindValue.Reference);
        Memory c = await SeedMemoryAsync(group.Id, "C", "Subject C", "Claim C", MemoryVersion.KindValue.Reference);
        Memory d = await SeedMemoryAsync(group.Id, "D", "Subject D", "Claim D", MemoryVersion.KindValue.Decision);

        (await Graph.CreateAsync(a.Uuid, b.Uuid, MemoryRelation.DependsOn, "a to b", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(b.Uuid, c.Uuid, MemoryRelation.DependsOn, "b to c", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(c.Uuid, d.Uuid, MemoryRelation.RelatesTo, "c to d", Ct)).ShouldBeTrue();

        return new Chain(group.Uuid, a.Uuid, b.Uuid, c.Uuid, d.Uuid);
    }

    private async Task<MemoryGroup> SeedGroupAsync(
        string scopeDimension = MemoryGroup.ScopeDimensionValue.Product)
    {
        MemoryGroup group = TestEntities.NewGroup(scopeDimension);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        return group;
    }

    private async Task<Memory> SeedMemoryAsync(
        long groupId,
        string name,
        string description,
        string? statement = null,
        string? kind = null)
    {
        Memory memory = TestEntities.NewMemory(groupId, name, description);
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);

        Db.MemoryVersions.Add(TestEntities.NewVersion(memory.Id, 1, statement ?? $"Claim {name}", kind: kind));
        await Db.SaveChangesAsync(Ct);
        return memory;
    }
}
