using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Retrieval;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

public sealed class TicketTraversalTests(AspireFixture aspire) : PersistenceTestBase(aspire)
{
    private NpgsqlTicketGraph Graph => new(Db);

    [Fact]
    public async Task EpicDescendants_IncludeAnchor_DeduplicateGroups_AndSelectOnlyCurrentNonProposedMemories()
    {
        MemoryGroup epic = await GroupAsync("product", Id("epic"));
        MemoryGroup shared = await GroupAsync("product", Id("story"), Id("task"));
        Memory anchor = await MemoryAsync(epic);
        Memory current = await MemoryAsync(shared);
        Db.MemoryVersions.Add(TestEntities.NewVersion(current.Id, 2, "Old claim", isCurrent: false));
        Memory proposed = await MemoryAsync(shared, status: "proposed");
        Memory reference = await MemoryAsync(shared, kind: "reference");
        Memory versionless = TestEntities.NewMemory(shared.Id, "No version", "No version");
        Db.Memories.Add(versionless);
        await Db.SaveChangesAsync(Ct);
        await ParentAsync("epic", "story");
        await ParentAsync("story", "task");

        TicketTraversalResult result = await Graph.TraverseAsync(Query("epic", 3), Ct);
        result.Paths.Select(p => p.Depth).ShouldBe([1, 2]);
        result.Paths.Last().Hops.Select(h => h.Child.Key).ShouldBe(["story", "task"]);
        result.Items.Select(m => m.Uuid).ShouldBe(new[] { anchor.Uuid, current.Uuid, reference.Uuid }.Order());
        result.Items.ShouldAllBe(m => m.IsCurrent && m.Version == 1 && m.Status != "proposed");
        result.Items.ShouldNotContain(m => m.Uuid == proposed.Uuid || m.Uuid == versionless.Uuid);
        result.Items.Single(m => m.Uuid == current.Uuid).Statement.ShouldBe("Current claim");
        result.Disclosure.ShouldBe(new TicketTraversalDisclosure(3, 50, 50, false, false, false));
        result.Disclosure.HierarchyCoverage.ShouldContain("Undeclared upstream hierarchy was not followed");
        result.Disclosure.HierarchyCoverage.ShouldContain("freshness and completeness are unverified");

        TicketTraversalResult narrowed = await Graph.TraverseAsync(Query("epic", 3) with { Kind = "decision", MemoryLimit = 2 }, Ct);
        narrowed.Items.Select(m => m.Uuid).ShouldBe(new[] { anchor.Uuid, current.Uuid }.Order());
        narrowed.Disclosure.MemoryLimitReached.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("product")]
    public async Task HiddenCrossGroupBranches_DropWholeRoutes_AndDoNotChangeVisibleResponseOrCaps(string? scope)
    {
        MemoryGroup root = await GroupAsync("product", Id("root"));
        MemoryGroup visible = await GroupAsync("product", Id("visible"));
        await MemoryAsync(root);
        await MemoryAsync(visible);
        await ParentAsync("root", "visible");
        TicketTraversalQuery query = Query("root", 1, scope) with { PathLimit = 1, MemoryLimit = 2 };
        TicketTraversalResult before = await Graph.TraverseAsync(query, Ct);
        before.Paths.Count.ShouldBe(1);
        before.Disclosure.ShouldBe(new TicketTraversalDisclosure(1, 1, 2, false, false, false));

        MemoryGroup hidden = await GroupAsync("program", Id("hidden"), Id("hidden-endpoint"));
        MemoryGroup beyond = await GroupAsync("product", Id("beyond"));
        await MemoryAsync(hidden);
        await MemoryAsync(beyond);
        await ParentAsync("root", "hidden");
        await ParentAsync("hidden", "beyond");
        await ParentAsync("visible", "hidden-endpoint");
        TicketTraversalResult after = await Graph.TraverseAsync(query, Ct);
        JsonSerializer.Serialize(after).ShouldBe(JsonSerializer.Serialize(before));

        TicketTraversalResult deep = await Graph.TraverseAsync(query with { MaxDepth = 3, PathLimit = 50 }, Ct);
        deep.Paths.SelectMany(p => p.Hops).Select(h => h.Child.Key).ShouldBe(["visible"]);
        deep.Items.Select(m => m.Uuid).ShouldBe(before.Items.Select(m => m.Uuid));
        deep.Disclosure.DepthLimitReached.ShouldBeFalse();

        TicketTraversalResult hiddenAnchor = await Graph.TraverseAsync(Query("hidden", 3, scope), Ct);
        hiddenAnchor.Paths.ShouldBeEmpty();
        hiddenAnchor.Items.ShouldBeEmpty();
        hiddenAnchor.Disclosure.ShouldBe(new TicketTraversalDisclosure(3, 50, 50, false, false, false));
        hiddenAnchor.Disclosure.HierarchyCoverage.ShouldBe(before.Disclosure.HierarchyCoverage);
    }

    [Fact]
    public async Task LiveScopeChange_HidesAndRestoresIntermediate_ExplicitConsentNarrowsMemoriesNotVisibleHops()
    {
        MemoryGroup root = await GroupAsync("product", Id("root"));
        MemoryGroup middle = await GroupAsync("customer", Id("middle"));
        MemoryGroup leaf = await GroupAsync("product", Id("leaf"));
        Memory rootMemory = await MemoryAsync(root);
        Memory middleMemory = await MemoryAsync(middle);
        Memory leafMemory = await MemoryAsync(leaf);
        await ParentAsync("root", "middle");
        await ParentAsync("middle", "leaf");
        TicketTraversalQuery query = Query("root", 3, "product");
        TicketTraversalResult visible = await Graph.TraverseAsync(query, Ct);
        visible.Paths.Last().Hops.Select(h => h.Child.Key).ShouldBe(["middle", "leaf"]);
        visible.Items.Select(m => m.Uuid).ShouldBe(new[] { rootMemory.Uuid, leafMemory.Uuid }.Order());

        middle.ScopeDimension = "program";
        await Db.SaveChangesAsync(Ct);
        TicketTraversalResult hidden = await Graph.TraverseAsync(query, Ct);
        hidden.Paths.ShouldBeEmpty();
        hidden.Items.Select(m => m.Uuid).ShouldBe([rootMemory.Uuid]);
        TicketTraversalResult consent = await Graph.TraverseAsync(Query("root", 3, "program"), Ct);
        consent.Paths.Count.ShouldBe(2);
        consent.Items.Single().Uuid.ShouldBe(middleMemory.Uuid);
        consent.Items.Single().ScopeDimension.ShouldBe("program");

        middle.ScopeDimension = "customer";
        await Db.SaveChangesAsync(Ct);
        JsonSerializer.Serialize(await Graph.TraverseAsync(query, Ct)).ShouldBe(JsonSerializer.Serialize(visible));
    }

    [Theory]
    [InlineData(TraversalDirection.Outbound, "leaf")]
    [InlineData(TraversalDirection.Inbound, "root")]
    [InlineData(TraversalDirection.Either, "leaf,root,sibling")]
    public async Task Directions_PreserveWrittenHopOrientation_AndEitherReachesSibling(TraversalDirection direction, string endpoints)
    {
        MemoryGroup group = await GroupAsync("product", Id("root"), Id("middle"), Id("leaf"), Id("sibling"));
        Memory memory = await MemoryAsync(group);
        await ParentAsync("root", "middle");
        await ParentAsync("middle", "leaf");
        await ParentAsync("root", "sibling");
        TicketTraversalResult result = await Graph.TraverseAsync(Query("middle", 3) with { Direction = direction }, Ct);
        result.Paths.Select(p => p.Hops.Last()).Select(h => h.Child.Key == "middle" ? h.Parent.Key : h.Child.Key)
            .Order().ShouldBe(endpoints.Split(',').Order());
        result.Paths.SelectMany(p => p.Hops).ShouldAllBe(h =>
            (h.Parent.Key == "root" && (h.Child.Key == "middle" || h.Child.Key == "sibling"))
            || (h.Parent.Key == "middle" && h.Child.Key == "leaf"));
        result.Items.Single().Uuid.ShouldBe(memory.Uuid);
        result.Disclosure.DepthLimitReached.ShouldBeFalse();
    }

    [Theory]
    [InlineData("'\\\"\n$q$ @hidden $1 }::vertex", "}::edge, {\"id\": 1}")]
    [InlineData("  Exact CASE  ", "quote ' slash \\ newline\n ::vertex @kind $q$")]
    public async Task HostileIdentityAndMetadata_RoundTripAsJsonWithoutRewriting(string identity, string reason)
    {
        var parent = new TicketIdentity(identity, "parent-" + identity);
        var child = new TicketIdentity("child-" + identity, identity);
        await GroupAsync("product", parent, child);
        var observed = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(3));
        var change = new TicketParentChange(child, parent, null, reason, identity, observed);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await Graph.ChangeParentAsync(change, Ct);
        TicketTraversalResult result = await Graph.TraverseAsync(new TicketTraversalQuery { Anchor = parent, MaxDepth = 1 }, Ct);
        TicketHierarchyHop hop = result.Paths.Single().Hops.Single();
        hop.Parent.ShouldBe(parent);
        hop.Child.ShouldBe(child);
        hop.Reason.ShouldBe(reason);
        hop.Source.ShouldBe(identity);
        hop.ObservedAt.ShouldBe(observed);
        hop.RecordedAt.ShouldBeInRange(before, DateTimeOffset.UtcNow);
        hop.RecordedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrAmbiguousOwner_FailsClosedForAnchorAndIntermediate(bool ambiguous)
    {
        MemoryGroup root = await GroupAsync("product", Id("root"));
        MemoryGroup middle = await GroupAsync("product", Id("middle"));
        MemoryGroup leaf = await GroupAsync("product", Id("leaf"));
        Memory rootMemory = await MemoryAsync(root);
        await MemoryAsync(middle);
        await MemoryAsync(leaf);
        await ParentAsync("root", "middle");
        await ParentAsync("middle", "leaf");
        (await Graph.TraverseAsync(Query("root", 3), Ct)).Paths.Count.ShouldBe(2);

        // Simulate legacy/corrupt ownership while preserving the graph route that must fail closed.
        await Db.Database.ExecuteSqlRawAsync("ALTER TABLE public.memory_group DISABLE TRIGGER trg_ticket_graph_membership", Ct);
        try
        {
            if (ambiguous)
                await GroupAsync("program", Id("middle"));
            else
            {
                middle.Tickets = [];
                await Db.SaveChangesAsync(Ct);
            }
        }
        finally
        {
            await Db.Database.ExecuteSqlRawAsync("ALTER TABLE public.memory_group ENABLE TRIGGER trg_ticket_graph_membership", Ct);
        }

        TicketTraversalResult result = await Graph.TraverseAsync(Query("root", 1) with { PathLimit = 1, MemoryLimit = 1 }, Ct);
        result.Paths.ShouldBeEmpty();
        result.Items.Single().Uuid.ShouldBe(rootMemory.Uuid);
        result.Disclosure.ShouldBe(new TicketTraversalDisclosure(1, 1, 1, false, false, false));
        foreach (string key in new[] { "middle", "never-owned" })
        {
            TicketTraversalResult empty = await Graph.TraverseAsync(Query(key, 3), Ct);
            empty.Paths.ShouldBeEmpty();
            empty.Items.ShouldBeEmpty();
            empty.Disclosure.ShouldBe(new TicketTraversalDisclosure(3, 50, 50, false, false, false));
        }
    }

    [Fact]
    public async Task Caps_AreDeterministic_DepthFlagRequiresExtraVisibleHop_AndMemoriesUseSelectedPaths()
    {
        MemoryGroup root = await GroupAsync("product", Id("root"));
        MemoryGroup a = await GroupAsync("product", Id("a"));
        MemoryGroup upper = await GroupAsync("product", Id("Z"));
        MemoryGroup grandchild = await GroupAsync("product", Id("deep"));
        Memory rootMemory = await MemoryAsync(root);
        Memory aMemory = await MemoryAsync(a);
        Memory upperMemory = await MemoryAsync(upper);
        await MemoryAsync(grandchild);
        // Reverse lexical insertion order makes an accidental edge-id ordering observable.
        await ParentAsync("root", "a");
        await ParentAsync("root", "Z");
        TicketTraversalQuery query = Query("root", 1) with { PathLimit = 2, MemoryLimit = 3 };
        TicketTraversalResult exact = await Graph.TraverseAsync(query, Ct);
        exact.Paths.Select(p => p.Hops.Single().Child.Key).ShouldBe(["Z", "a"]);
        exact.Disclosure.ShouldBe(new TicketTraversalDisclosure(1, 2, 3, false, false, false));
        await ParentAsync("a", "deep");
        TicketTraversalResult bounded = await Graph.TraverseAsync(query, Ct);
        bounded.Disclosure.ShouldBe(new TicketTraversalDisclosure(1, 2, 3, true, false, false));
        bounded.Items.Select(m => m.Uuid).ShouldBe(new[] { rootMemory.Uuid, aMemory.Uuid, upperMemory.Uuid }.Order());

        TicketTraversalQuery cappedQuery = query with { PathLimit = 1, MemoryLimit = 1 };
        TicketTraversalResult capped = await Graph.TraverseAsync(cappedQuery, Ct);
        capped.Paths.Single().Hops.Single().Child.Key.ShouldBe("Z");
        capped.Items.Single().Uuid.ShouldBe(new[] { rootMemory.Uuid, upperMemory.Uuid }.Order().First());
        capped.Disclosure.ShouldBe(new TicketTraversalDisclosure(1, 1, 1, true, true, true));
        JsonSerializer.Serialize(await Graph.TraverseAsync(cappedQuery, Ct)).ShouldBe(JsonSerializer.Serialize(capped));
        TicketTraversalResult deep = await Graph.TraverseAsync(query with { MaxDepth = 5, PathLimit = 200, MemoryLimit = 200 }, Ct);
        deep.Paths.Select(p => p.Depth).ShouldBe([1, 1, 2]);
        deep.Disclosure.DepthLimitReached.ShouldBeFalse();
    }

    [Fact]
    public async Task TicketAddition_AssociatesExistingAndFutureMemories_WithoutMembershipFanout()
    {
        MemoryGroup root = await GroupAsync("product", Id("root"));
        MemoryGroup group = await GroupAsync("product");
        Memory existing = await MemoryAsync(group);
        group.Tickets.Add(TicketDocument.Create("jira", "added", "https://tracker/added"));
        await Db.SaveChangesAsync(Ct);
        TicketTraversalResult standalone = await Graph.TraverseAsync(Query("added", 2), Ct);
        standalone.Paths.ShouldBeEmpty();
        standalone.Items.Single().Uuid.ShouldBe(existing.Uuid);
        await ParentAsync("root", "added");
        Memory future = await MemoryAsync(group);
        TicketTraversalResult result = await Graph.TraverseAsync(Query("root", 2), Ct);
        result.Items.Select(m => m.Uuid).ShouldBe(new[] { existing.Uuid, future.Uuid }.Order());
        result.Paths.Single().Hops.Single().Child.ShouldBe(Id("added"));
        await using NpgsqlCommand command = DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM memory_graph."Ticket"),
                   (SELECT count(*) FROM memory_graph."TICKET_PARENT"),
                   (SELECT count(*) FROM memory_graph._ag_label_vertex),
                   (SELECT count(*) FROM memory_graph._ag_label_edge)
            """);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        reader.GetInt64(0).ShouldBe(2);
        reader.GetInt64(1).ShouldBe(1);
        reader.GetInt64(2).ShouldBe(2);
        reader.GetInt64(3).ShouldBe(1);
    }

    [Fact]
    public async Task DirectStore_RejectsInvalidDepthCapsAndDirection()
    {
        TicketTraversalQuery valid = Query("missing", 1);
        TicketTraversalQuery[] invalid =
        [
            valid with { MaxDepth = 0 }, valid with { MaxDepth = 6 },
            valid with { PathLimit = 0 }, valid with { PathLimit = 201 },
            valid with { MemoryLimit = 0 }, valid with { MemoryLimit = 201 },
            valid with { Direction = (TraversalDirection)99 },
        ];
        foreach (TicketTraversalQuery query in invalid)
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Graph.TraverseAsync(query, Ct));
    }

    private static TicketIdentity Id(string key) => new("jira", key);

    private static TicketTraversalQuery Query(string key, int depth, string? scope = null) => new()
    {
        Anchor = Id(key), MaxDepth = depth,
        RequiredScopeDimension = MemoryScopeFilter.Plan(scope, hasGroupContext: false).RequiredDimension,
        HiddenDimensions = MemoryScopeFilter.HiddenDimensions(scope, hasGroupContext: false),
    };

    private Task<bool> ParentAsync(string parent, string child) =>
        Graph.ChangeParentAsync(new TicketParentChange(Id(child), Id(parent), null, "Declared parent", "practitioner"), Ct);

    private async Task<MemoryGroup> GroupAsync(string scope, params TicketIdentity[] tickets)
    {
        MemoryGroup group = TestEntities.NewGroup(scope, tickets: tickets.Select(t =>
            TicketDocument.Create(t.Provider, t.Key, "https://tracker/ticket")).ToList());
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        return group;
    }

    private async Task<Memory> MemoryAsync(MemoryGroup group, string kind = "decision", string status = "approved")
    {
        Memory memory = TestEntities.NewMemory(group.Id, "Test memory", $"Subject {Guid.NewGuid():N}");
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);
        MemoryVersion version = TestEntities.NewVersion(memory.Id, 1, "Current claim", kind: kind);
        version.Status = status;
        Db.MemoryVersions.Add(version);
        await Db.SaveChangesAsync(Ct);
        return memory;
    }
}
