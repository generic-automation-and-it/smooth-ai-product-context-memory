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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbientReplacementFailure_RestoresDeclarationAndLeavesOuterCommittable(bool cancel)
    {
        await SeedAsync("child", "parent", "other");
        await Graph.ChangeParentAsync(Change("child", "parent"), Ct);
        string original = await EdgePropertiesAsync();
        await Db.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION public.block_ticket_replacement() RETURNS boolean AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock(734921, 2);
                RETURN false;
            END;
            $$ LANGUAGE plpgsql;
            ALTER TABLE memory_graph."TICKET_PARENT" ADD CONSTRAINT fail_replacement
                CHECK (public.block_ticket_replacement()) NOT VALID;
            """, Ct);
        await using var blocker = await DataSource.OpenConnectionAsync(Ct);
        await using var blockingTransaction = await blocker.BeginTransactionAsync(Ct);
        await using (var block = new NpgsqlCommand("SELECT pg_advisory_xact_lock(734921, 2)", blocker, blockingTransaction))
        {
            await block.ExecuteNonQueryAsync(Ct);
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(Ct);
        await Db.Database.ExecuteSqlRawAsync("UPDATE public.memory_group SET repo = 'outer-work'", Ct);
        int pid = ((NpgsqlConnection)Db.Database.GetDbConnection()).ProcessID;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<bool> replacement = Graph.ChangeParentAsync(Change("child", "other", "parent"), cancellation.Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                await using NpgsqlCommand blocked = DataSource.CreateCommand("SELECT @blocker = ANY(pg_blocking_pids(@pid))");
                blocked.Parameters.AddWithValue("blocker", blocker.ProcessID);
                blocked.Parameters.AddWithValue("pid", pid);
                if ((bool)(await blocked.ExecuteScalarAsync(timeout.Token))!) break;
                await Task.Delay(20, timeout.Token);
            }

            if (cancel)
            {
                await cancellation.CancelAsync();
                await Should.ThrowAsync<OperationCanceledException>(() => replacement);
            }
            else
            {
                await blockingTransaction.CommitAsync(Ct);
                PostgresException error = await Should.ThrowAsync<PostgresException>(() => replacement);
                error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            }

            await Db.Database.ExecuteSqlRawAsync("ALTER TABLE memory_graph.\"TICKET_PARENT\" DROP CONSTRAINT fail_replacement", Ct);
            (await Graph.ChangeParentAsync(Change("child", "parent", "parent"), Ct)).ShouldBeFalse();
            await transaction.CommitAsync(Ct);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await replacement; }
            catch (OperationCanceledException) { }
            catch (PostgresException) { }
        }

        (await EdgePropertiesAsync()).ShouldBe(original);
        (await Db.MemoryGroups.AsNoTracking().SingleAsync(Ct)).Repo.ShouldBe("outer-work");
    }

    [Theory]
    [InlineData("child", false)]
    [InlineData("other", false)]
    [InlineData("parent", false)]
    [InlineData("child", true)]
    [InlineData("other", true)]
    [InlineData("parent", true)]
    public async Task MissingOrDuplicateOwnedVertex_RejectsBeforeChangingDeclaration(string key, bool duplicate)
    {
        await SeedAsync("child", "parent", "other");
        await Graph.ChangeParentAsync(Change("child", "parent"), Ct);
        string original = await EdgePropertiesAsync();
        string sql = duplicate
            ? "INSERT INTO memory_graph.\"Ticket\" (properties) SELECT properties FROM memory_graph.\"Ticket\" WHERE properties::text::jsonb->>'key' = @key"
            : "DELETE FROM memory_graph.\"Ticket\" WHERE properties::text::jsonb->>'key' = @key";
        await using (NpgsqlCommand corrupt = DataSource.CreateCommand(sql))
        {
            corrupt.Parameters.AddWithValue("key", key);
            await corrupt.ExecuteNonQueryAsync(Ct);
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(Ct);
        await Should.ThrowAsync<ConflictException>(() => Graph.ChangeParentAsync(Change("child", "other", "parent"), Ct));
        await transaction.CommitAsync(Ct);
        (await EdgePropertiesAsync()).ShouldBe(original);
        (await CountEdgesAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData("[{\"provider\":123,\"key\":\"parent\"}]", "123", "parent")]
    [InlineData("[{\"provider\":\"jira\",\"key\":123}]", "jira", "123")]
    [InlineData("[{\"provider\":true,\"key\":\"parent\"}]", "true", "parent")]
    [InlineData("[{\"provider\":\"jira\",\"key\":null}]", "jira", "parent")]
    [InlineData("[null,123,\"parent\",{}]", "jira", "parent")]
    [InlineData("null", "jira", "parent")]
    [InlineData("123", "jira", "parent")]
    [InlineData("{}", "jira", "parent")]
    public async Task CorruptMembership_DoesNotBecomeStringIdentity(string json, string provider, string key)
    {
        await SeedAsync("child");
        var identity = new TicketIdentity(provider, key);
        var owner = TestEntities.NewGroup(tickets: [TicketDocument.Create(provider, key, "")]);
        Db.MemoryGroups.Add(owner);
        await Db.SaveChangesAsync(Ct);
        await Db.Database.ExecuteSqlRawAsync("ALTER TABLE public.memory_group DISABLE TRIGGER trg_ticket_graph_membership", Ct);
        try
        {
            await Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.memory_group SET tickets = {json}::jsonb WHERE id = {owner.Id}", Ct);
        }
        finally
        {
            await Db.Database.ExecuteSqlRawAsync("ALTER TABLE public.memory_group ENABLE TRIGGER trg_ticket_graph_membership", Ct);
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(Ct);
        await Should.ThrowAsync<NotFoundException>(() => Graph.ChangeParentAsync(Change("child", "parent") with { Parent = identity }, Ct));
        (await Graph.ChangeParentAsync(Change("child", null), Ct)).ShouldBeFalse();
        await transaction.CommitAsync(Ct);
        (await CountEdgesAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(System.Data.IsolationLevel.RepeatableRead)]
    [InlineData(System.Data.IsolationLevel.Serializable)]
    [InlineData(System.Data.IsolationLevel.ReadUncommitted)]
    public async Task Lock_RejectsUnsupportedIsolationBeforeSql(System.Data.IsolationLevel isolation)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(isolation, Ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => Graph.LockAsync(cancelled.Token));
        await Db.MemoryGroups.AsNoTracking().CountAsync(Ct);
        await transaction.CommitAsync(Ct);
    }

    [Fact]
    public async Task RepeatableRead_StaleSnapshotCannotAddASecondParent()
    {
        await SeedAsync("child", "parent", "other");
        await using var otherDb = NewContext();
        await using var transaction = await Db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, Ct);
        await Db.MemoryGroups.AsNoTracking().CountAsync(Ct);
        await new NpgsqlTicketGraph(otherDb).ChangeParentAsync(Change("child", "parent"), Ct);

        await Should.ThrowAsync<InvalidOperationException>(() => Graph.ChangeParentAsync(Change("child", "other"), Ct));
        await transaction.CommitAsync(Ct);

        (await CountEdgesAsync()).ShouldBe(1);
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
