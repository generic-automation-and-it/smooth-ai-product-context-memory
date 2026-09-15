using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

public sealed class TicketGraphMutationTests(AspireFixture aspire) : PersistenceTestBase(aspire)
{
    private NpgsqlTicketGraph Graph => new(Db);

    [Fact]
    public async Task SetReplaceReparentRemove_EnforcesExpectationsAndPreservesMetadata()
    {
        await SeedAsync("parent", "child", "other");
        var observed = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(3));
        var change = Change("child", "parent") with { ObservedAt = observed, Reason = "'quoted' $q$ }::edge \\ reason", Source = "manual\nsource" };
        (await Graph.ChangeParentAsync(change, Ct)).ShouldBeTrue();
        string original = await EdgePropertiesAsync();
        await Should.ThrowAsync<ConflictException>(() => Graph.ChangeParentAsync(change, Ct));
        (await Graph.ChangeParentAsync(change with { ExpectedParent = Id("parent") }, Ct)).ShouldBeFalse();
        (await EdgePropertiesAsync()).ShouldBe(original);
        using (var json = System.Text.Json.JsonDocument.Parse(original))
        {
            json.RootElement.GetProperty("reason").GetString().ShouldBe(change.Reason);
            json.RootElement.GetProperty("source").GetString().ShouldBe(change.Source);
            DateTimeOffset.Parse(json.RootElement.GetProperty("observedAt").GetString()!).ShouldBe(observed);
            DateTimeOffset.Parse(json.RootElement.GetProperty("recordedAt").GetString()!).Offset.ShouldBe(TimeSpan.Zero);
        }

        await Should.ThrowAsync<ConflictException>(() => Graph.ChangeParentAsync(change with { ExpectedParent = Id("other") }, Ct));
        await Should.ThrowAsync<ConflictException>(() => Graph.ChangeParentAsync(change with { Reason = "changed" }, Ct));
        (await Graph.ChangeParentAsync(change with { ExpectedParent = Id("parent"), Reason = "changed" }, Ct)).ShouldBeTrue();
        (await Graph.ChangeParentAsync(Change("child", "other", "parent"), Ct)).ShouldBeTrue();
        string reparented = await EdgePropertiesAsync();
        await Should.ThrowAsync<ConflictException>(() => Graph.ChangeParentAsync(Change("child", "other", "parent"), Ct));
        (await Graph.ChangeParentAsync(Change("child", "other", "other"), Ct)).ShouldBeFalse();
        (await EdgePropertiesAsync()).ShouldBe(reparented);
        await Should.ThrowAsync<ConflictException>(() => Graph.ChangeParentAsync(Change("child", null, "parent"), Ct));
        (await Graph.ChangeParentAsync(Change("child", null, "other"), Ct)).ShouldBeTrue();
        await Should.ThrowAsync<ConflictException>(() => Graph.ChangeParentAsync(Change("child", null, "other"), Ct));
        (await Graph.ChangeParentAsync(Change("child", null), Ct)).ShouldBeFalse();
        (await CountEdgesAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task CycleCheck_SeesBeyondTraversalDepth_AndIgnoresScope()
    {
        await SeedAsync(Enumerable.Range(0, 9).Select(i => i.ToString()).ToArray());
        for (int i = 1; i < 9; i++)
        {
            await Graph.ChangeParentAsync(Change(i.ToString(), (i - 1).ToString()), Ct);
        }

        await Should.ThrowAsync<ConflictException>(() => Graph.ChangeParentAsync(Change("0", "8"), Ct));
        await Should.ThrowAsync<ConflictException>(() => Graph.ChangeParentAsync(Change("0", "0"), Ct));
        (await CountEdgesAsync()).ShouldBe(8);
    }

    [Fact]
    public async Task MissingOwner_FailsGenerically_AndDoesNotCreateVertices()
    {
        await SeedAsync("child");
        NotFoundException error = await Should.ThrowAsync<NotFoundException>(() => Graph.ChangeParentAsync(Change("child", "secret-parent"), Ct));
        error.Message.ShouldNotContain("secret-parent");
        (await CountEdgesAsync()).ShouldBe(0);
        await using NpgsqlCommand count = DataSource.CreateCommand("SELECT count(*) FROM memory_graph.\"Ticket\"");
        Convert.ToInt64(await count.ExecuteScalarAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task AmbiguousOwner_FailsClosed_EvenWithAnExistingVertex()
    {
        await SeedAsync("child", "parent");
        await Db.Database.ExecuteSqlRawAsync("ALTER TABLE public.memory_group DISABLE TRIGGER trg_ticket_graph_membership", Ct);
        await SeedAsync("child");
        await Db.Database.ExecuteSqlRawAsync("ALTER TABLE public.memory_group ENABLE TRIGGER trg_ticket_graph_membership", Ct);
        await Should.ThrowAsync<ConflictException>(() => Graph.ChangeParentAsync(Change("child", "parent"), Ct));
        (await CountEdgesAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("key")]
    [InlineData("long-provider")]
    [InlineData("long-key")]
    [InlineData("reason")]
    [InlineData("source")]
    [InlineData("long-reason")]
    [InlineData("long-source")]
    [InlineData("nul")]
    public async Task DirectStore_ValidatesBeforeDatabaseAccess(string invalid)
    {
        TicketParentChange change = Change("child", "parent");
        change = invalid switch
        {
            "provider" => change with { Child = new(" ", "child") },
            "key" => change with { Parent = new("jira", "") },
            "long-provider" => change with { ExpectedParent = new(new string('p', 513), "key") },
            "long-key" => change with { Child = new("jira", new string('k', 513)) },
            "reason" => change with { Reason = " " },
            "source" => change with { Source = "" },
            "long-reason" => change with { Reason = new string('r', 4001) },
            "long-source" => change with { Source = new string('s', 4001) },
            _ => change with { Child = new("jira", "bad\0key") },
        };
        await Should.ThrowAsync<ArgumentException>(() => Graph.ChangeParentAsync(change, Ct));
        Db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public async Task AmbientRollback_RestoresReplacedDeclaration()
    {
        await SeedAsync("child", "parent", "other");
        await Graph.ChangeParentAsync(Change("child", "parent"), Ct);
        string before = await EdgePropertiesAsync();
        await using (var transaction = await Db.Database.BeginTransactionAsync(Ct))
        {
            await Graph.ChangeParentAsync(Change("child", "other", "parent"), Ct);
            await transaction.RollbackAsync(Ct);
        }

        (await EdgePropertiesAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task CompetingParents_Serialize_OnlyOneWins()
    {
        await SeedAsync("child", "parent", "other");
        await using var otherDb = NewContext();
        Task<bool> first = AttemptAsync(Graph, Change("child", "parent"));
        Task<bool> second = AttemptAsync(new NpgsqlTicketGraph(otherDb), Change("child", "other"));
        bool[] results = await Task.WhenAll(first, second);
        results.Count(r => r).ShouldBe(1);
        (await CountEdgesAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentCycle_Serializes_OnlyOneDirectionWins()
    {
        await SeedAsync("child", "parent");
        await using var otherDb = NewContext();
        bool[] results = await Task.WhenAll(
            AttemptAsync(Graph, Change("child", "parent")),
            AttemptAsync(new NpgsqlTicketGraph(otherDb), Change("parent", "child")));
        results.Count(r => r).ShouldBe(1);
        (await CountEdgesAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task GroupMembershipChange_WaitsForHierarchyLock_ThenCleansUp()
    {
        await SeedAsync("child", "parent");
        await using var otherDb = NewContext();
        await using (var transaction = await Db.Database.BeginTransactionAsync(Ct))
        {
            await Graph.LockAsync(Ct);
            Task<int> remove = otherDb.Database.ExecuteSqlRawAsync("UPDATE public.memory_group SET tickets = '[]'::jsonb", Ct);
            await Graph.ChangeParentAsync(Change("child", "parent"), Ct);
            remove.IsCompleted.ShouldBeFalse();
            await transaction.CommitAsync(Ct);
            await remove;
        }

        (await CountEdgesAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Lock_RequiresExplicitTransaction() =>
        await Should.ThrowAsync<InvalidOperationException>(() => Graph.LockAsync(Ct));

    private async Task SeedAsync(params string[] keys)
    {
        Db.MemoryGroups.Add(TestEntities.NewGroup(tickets: keys.Select(k => TicketDocument.Create("jira", k, "https://tracker/ticket")).ToList()));
        await Db.SaveChangesAsync(Ct);
    }

    private SmoothAiProductContextMemoryDbContext NewContext() => new(
        new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
            .UseNpgsql(DataSource, options => options.UseSmoothAiProductContextMemoryHistory()).Options);

    private async Task<bool> AttemptAsync(NpgsqlTicketGraph graph, TicketParentChange change)
    {
        try { return await graph.ChangeParentAsync(change, Ct); }
        catch (ConflictException) { return false; }
    }

    private static TicketIdentity Id(string key) => new("jira", key);

    private static TicketParentChange Change(string child, string? parent, string? expected = null) =>
        new(Id(child), parent is null ? null : Id(parent), expected is null ? null : Id(expected), "Declared parent", "practitioner");

    private async Task<long> CountEdgesAsync()
    {
        await using NpgsqlCommand command = DataSource.CreateCommand("SELECT count(*) FROM memory_graph.\"TICKET_PARENT\"");
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
    }

    private async Task<string> EdgePropertiesAsync()
    {
        await using NpgsqlCommand command = DataSource.CreateCommand("SELECT properties::text FROM memory_graph.\"TICKET_PARENT\"");
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }
}
