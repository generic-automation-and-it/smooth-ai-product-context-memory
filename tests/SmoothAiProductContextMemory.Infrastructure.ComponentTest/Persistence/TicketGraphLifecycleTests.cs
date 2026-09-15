using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

public sealed class TicketGraphLifecycleTests(AspireFixture aspire) : PersistenceTestBase(aspire)
{
    private const string PreviousMigration = "20260914120000_AddGraphPropertyIndexes";
    private NpgsqlTicketGraph Graph => new(Db);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongUtf8Identities_PersistExactly_AcrossCreationAndPopulatedUpgrade(bool upgrade)
    {
        if (upgrade) await Db.GetService<IMigrator>().MigrateAsync(PreviousMigration, Ct);
        var random = new Random(734921);
        string provider = new(Enumerable.Range(0, 4096).Select(_ => (char)random.Next(0x4e00, 0x9fff)).ToArray());
        string key = new(Enumerable.Range(0, 4096).Select(_ => (char)random.Next(0x4e00, 0x9fff)).ToArray());
        System.Text.Encoding.UTF8.GetByteCount(provider).ShouldBeGreaterThan(2704);
        System.Text.Encoding.UTF8.GetByteCount(key).ShouldBeGreaterThan(2704);
        var group = TestEntities.NewGroup(tickets:
        [
            TicketDocument.Create(provider, key, "https://tracker/one"),
            TicketDocument.Create(provider, key, "https://tracker/duplicate"),
            TicketDocument.Create(provider, key + "x", "https://tracker/two"),
            TicketDocument.Create(provider + "x", key, "https://tracker/three"),
        ]);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        await Db.GetService<IMigrator>().MigrateAsync(cancellationToken: Ct);

        await using (var restarted = new SmoothAiProductContextMemoryDbContext(
            new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
                .UseNpgsql(DataSource, options => options.UseSmoothAiProductContextMemoryHistory()).Options))
        {
            await restarted.Database.MigrateAsync(Ct);
            MemoryGroup stored = await restarted.MemoryGroups.AsNoTracking().SingleAsync(Ct);
            stored.Tickets[0].Provider.ShouldBe(provider);
            stored.Tickets[0].Key.ShouldBe(key);
        }

        (await CountAsync("Ticket")).ShouldBe(3);
        string cypher = $"MATCH (t:Ticket) WHERE t.provider = {CypherLiteral.Quote(provider)} AND t.key = {CypherLiteral.Quote(key)} RETURN count(t)";
        await using (NpgsqlCommand command = DataSource.CreateCommand(
            $"SELECT n::text FROM ag_catalog.cypher('memory_graph', {CypherLiteral.DollarWrap(cypher)}) AS (n ag_catalog.agtype)"))
        {
            (await command.ExecuteScalarAsync(Ct)).ShouldBe("1");
        }

        group.Tickets.RemoveAll(t => t.Provider == provider && t.Key == key);
        await Db.SaveChangesAsync(Ct);
        (await CountAsync("Ticket")).ShouldBe(2);
        (await CountAsync("TICKET_PARENT")).ShouldBe(0);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("key")]
    public async Task ExactCypherPropertyPredicate_UsesHashIndexWithRecheck(string property)
    {
        await AddGroupAsync("anchor");
        await using var connection = await DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var setting = new NpgsqlCommand("SET LOCAL enable_seqscan = off; SET LOCAL enable_indexscan = off", connection, transaction))
        {
            await setting.ExecuteNonQueryAsync(Ct);
        }

        string cypher = $"MATCH (t:Ticket) WHERE t.{property} = {CypherLiteral.Quote(property == "provider" ? "jira" : "anchor")} RETURN id(t)";
        await using var command = new NpgsqlCommand(
            $"EXPLAIN (FORMAT JSON) SELECT id::text FROM ag_catalog.cypher('memory_graph', {CypherLiteral.DollarWrap(cypher)}) AS (id ag_catalog.agtype)",
            connection, transaction);
        string plan = (string)(await command.ExecuteScalarAsync(Ct))!;
        plan.ShouldContain($"ix_ticket_vertex_{property}");
        plan.ShouldContain("Recheck Cond");
    }

    [Fact]
    public async Task EmptyGroups_CreateExactIdentityOnlyVertices_WithoutInferringHierarchy()
    {
        string key = "'key' \\ \" $q$ }::vertex\n";
        var group = TestEntities.NewGroup(tickets:
        [
            TicketDocument.Create("jira", key, "https://tracker/one"),
            TicketDocument.Create("jira", key, "https://tracker/changed-url"),
            TicketDocument.Create("Jira", key, "https://tracker/two"),
            TicketDocument.Create("jira", " " + key, "https://tracker/three"),
        ]);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        (await CountAsync("Ticket")).ShouldBe(3);
        (await CountAsync("TICKET_PARENT")).ShouldBe(0);
        await using NpgsqlCommand command = DataSource.CreateCommand("SELECT properties::text FROM memory_graph.\"Ticket\"");
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            using var properties = System.Text.Json.JsonDocument.Parse(reader.GetString(0));
            properties.RootElement.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["key", "provider"]);
            properties.RootElement.GetProperty("key").GetString().ShouldBeOneOf(key, " " + key);
        }
    }

    [Fact]
    public async Task DuplicateOwners_RejectedByInsertAndUpdate_ButSameGroupDuplicatesAllowed()
    {
        await AddGroupAsync("shared");
        var other = await AddGroupAsync("other");
        other.Tickets.Add(Ticket("shared"));
        DbUpdateException update = await Should.ThrowAsync<DbUpdateException>(() => Db.SaveChangesAsync(Ct));
        ((PostgresException)update.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        Db.ChangeTracker.Clear();
        Db.MemoryGroups.Add(TestEntities.NewGroup(tickets: [Ticket("shared")]));
        DbUpdateException insert = await Should.ThrowAsync<DbUpdateException>(() => Db.SaveChangesAsync(Ct));
        ((PostgresException)insert.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        (await CountAsync("Ticket")).ShouldBe(2);
    }

    [Fact]
    public async Task ConcurrentDuplicateOwners_OnlyOneInsertCommits()
    {
        await using var other = new SmoothAiProductContextMemoryDbContext(
            new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
                .UseNpgsql(DataSource, options => options.UseSmoothAiProductContextMemoryHistory()).Options);
        Db.MemoryGroups.Add(TestEntities.NewGroup(tickets: [Ticket("same")]));
        other.MemoryGroups.Add(TestEntities.NewGroup(tickets: [Ticket("same")]));
        bool[] results = await Task.WhenAll(InsertAsync(Db), InsertAsync(other));
        results.Count(r => r).ShouldBe(1);
        (await CountAsync("Ticket")).ShouldBe(1);
    }

    [Fact]
    public async Task MembershipRemovalAndGroupDelete_CleanIncidentEdgesOnly()
    {
        var parent = await AddGroupAsync("parent");
        var middle = await AddGroupAsync("middle", "alias");
        await AddGroupAsync("child", "unrelated");
        await DeclareAsync("middle", "parent");
        await DeclareAsync("child", "middle");
        await DeclareAsync("unrelated", "child");
        middle.Tickets.RemoveAll(t => t.Key == "alias");
        await Db.SaveChangesAsync(Ct);
        (await CountAsync("Ticket")).ShouldBe(4);
        (await CountAsync("TICKET_PARENT")).ShouldBe(3);
        Db.MemoryGroups.Remove(middle);
        await Db.SaveChangesAsync(Ct);
        (await CountAsync("Ticket")).ShouldBe(3);
        (await CountAsync("TICKET_PARENT")).ShouldBe(1);
        parent.Tickets.Clear();
        await Db.SaveChangesAsync(Ct);
        (await CountAsync("Ticket")).ShouldBe(2);
    }

    [Fact]
    public async Task LastMemoryDelete_PreservesTicketHierarchy()
    {
        var group = await AddGroupAsync("parent", "child");
        var memory = TestEntities.NewMemory(group.Id, "Only", "Only memory");
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);
        await DeclareAsync("child", "parent");
        Db.Memories.Remove(memory);
        await Db.SaveChangesAsync(Ct);
        (await CountAsync("Ticket")).ShouldBe(2);
        (await CountAsync("TICKET_PARENT")).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultirowMembershipRemovalOrDelete_CleansAllVerticesAndEdges(bool delete)
    {
        await AddGroupAsync("parent");
        await AddGroupAsync("middle");
        await AddGroupAsync("child");
        await DeclareAsync("middle", "parent");
        await DeclareAsync("child", "middle");

        await Db.Database.ExecuteSqlRawAsync(delete
            ? "DELETE FROM public.memory_group"
            : "UPDATE public.memory_group SET tickets = '[]'::jsonb", Ct);

        (await CountAsync("Ticket")).ShouldBe(0);
        (await CountAsync("TICKET_PARENT")).ShouldBe(0);
        (await Db.MemoryGroups.AsNoTracking().CountAsync(Ct)).ShouldBe(delete ? 0 : 3);
    }

    [Fact]
    public async Task GroupDeleteFailure_RollsBackRelationalAndBothGraphs()
    {
        var group = await AddGroupAsync("parent", "child");
        var memory = TestEntities.NewMemory(group.Id, "One", "One subject");
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);
        var memoryGraph = new NpgsqlMemoryGraph(Db);
        await memoryGraph.CreateAsync(memory.Uuid, memory.Uuid, "relates_to", "memory reason", Ct);
        await DeclareAsync("child", "parent");
        await Db.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION public.fail_ticket_delete() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'forced after cleanup'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER zzz_fail_ticket_delete AFTER DELETE ON public.memory_group
                FOR EACH ROW EXECUTE FUNCTION public.fail_ticket_delete();
            """, Ct);
        Db.MemoryGroups.Remove(group);
        await Should.ThrowAsync<DbUpdateException>(() => Db.SaveChangesAsync(Ct));
        (await CountAsync("Ticket")).ShouldBe(2);
        (await CountAsync("TICKET_PARENT")).ShouldBe(1);
        (await memoryGraph.ListAllAsync(Ct)).Count.ShouldBe(1);
        (await Db.MemoryGroups.AsNoTracking().CountAsync(Ct)).ShouldBe(1);
        (await Db.Memories.AsNoTracking().CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Down_WaitsForHandlerLockWithoutBlockingItsGroupWrite()
    {
        await AddGroupAsync("parent", "child");
        await DeclareAsync("child", "parent");
        await using var migrating = new SmoothAiProductContextMemoryDbContext(
            new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
                .UseNpgsql(DataSource, options => options.UseSmoothAiProductContextMemoryHistory()).Options);
        await migrating.Database.OpenConnectionAsync(Ct);
        int migrationPid = ((NpgsqlConnection)migrating.Database.GetDbConnection()).ProcessID;
        await using var transaction = await Db.Database.BeginTransactionAsync(Ct);
        await Graph.LockAsync(Ct);
        Task migration = migrating.GetService<IMigrator>().MigrateAsync(PreviousMigration, Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using NpgsqlCommand blocked = DataSource.CreateCommand(
                "SELECT cardinality(pg_blocking_pids(@pid)) > 0");
            blocked.Parameters.AddWithValue("pid", migrationPid);
            if ((bool)(await blocked.ExecuteScalarAsync(timeout.Token))!) break;
            await Task.Delay(20, timeout.Token);
        }

        try
        {
            await Db.Database.ExecuteSqlRawAsync("UPDATE public.memory_group SET tickets = tickets", Ct);
            await transaction.CommitAsync(Ct);
            await migration;
        }
        finally
        {
            await transaction.DisposeAsync();
            try { await migration; }
            catch (Exception error) when (error.GetBaseException() is PostgresException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_TableLockContentionFailsFastAndCanRetry(bool upgrade)
    {
        await AddGroupAsync("parent", "child");
        await DeclareAsync("child", "parent");
        if (upgrade) await Db.GetService<IMigrator>().MigrateAsync(PreviousMigration, Ct);
        await using (var rawWriter = await DataSource.OpenConnectionAsync(Ct))
        await using (var transaction = await rawWriter.BeginTransactionAsync(Ct))
        {
            await using var command = new NpgsqlCommand(
                "LOCK TABLE public.memory_group IN ROW EXCLUSIVE MODE", rawWriter, transaction);
            await command.ExecuteNonQueryAsync(Ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            Exception error = await Should.ThrowAsync<Exception>(() => Db.GetService<IMigrator>()
                .MigrateAsync(upgrade ? null : PreviousMigration, timeout.Token));
            error.GetBaseException().ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.LockNotAvailable);
            await transaction.RollbackAsync(Ct);
        }

        if (!upgrade)
        {
            (await CountAsync("Ticket")).ShouldBe(2);
            (await CountAsync("TICKET_PARENT")).ShouldBe(1);
        }

        await Db.GetService<IMigrator>().MigrateAsync(upgrade ? null : PreviousMigration, Ct);
        if (upgrade)
        {
            (await CountAsync("Ticket")).ShouldBe(2);
            (await CountAsync("TICKET_PARENT")).ShouldBe(0);
        }
    }

    [Fact]
    public async Task PopulatedDownUp_WarnsAndDropsDeclarationsOnly_BackfillsEmptyGroupIdentities()
    {
        var group = await AddGroupAsync("parent", "child");
        await AddGroupAsync("empty-group-ticket");
        var memory = TestEntities.NewMemory(group.Id, "Memory", "Preserved subject");
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);
        var memoryGraph = new NpgsqlMemoryGraph(Db);
        await memoryGraph.CreateAsync(memory.Uuid, memory.Uuid, "relates_to", "preserved reason", Ct);
        await DeclareAsync("child", "parent");
        await Db.Database.OpenConnectionAsync(Ct);
        var connection = (NpgsqlConnection)Db.Database.GetDbConnection();
        var warnings = new List<string>();
        connection.Notice += (_, args) => warnings.Add(args.Notice.MessageText);
        await Db.GetService<IMigrator>().MigrateAsync(PreviousMigration, Ct);
        warnings.ShouldContain(w => w.Contains("declarations will be lost", StringComparison.Ordinal));
        await using (NpgsqlCommand labels = DataSource.CreateCommand(
            "SELECT count(*) FROM ag_catalog.ag_label WHERE graph = (SELECT graphid FROM ag_catalog.ag_graph WHERE name = 'memory_graph') AND name IN ('Ticket','TICKET_PARENT')"))
        {
            Convert.ToInt64(await labels.ExecuteScalarAsync(Ct)).ShouldBe(0);
        }

        (await memoryGraph.ListAllAsync(Ct)).Single().Reason.ShouldBe("preserved reason");
        (await CountAsync("Memory")).ShouldBe(1);
        MemoryGroup[] groups = await Db.MemoryGroups.AsNoTracking().ToArrayAsync(Ct);
        groups.Sum(g => g.Tickets.Count).ShouldBe(3);
        await Db.GetService<IMigrator>().MigrateAsync(cancellationToken: Ct);
        (await CountAsync("Ticket")).ShouldBe(3);
        (await CountAsync("TICKET_PARENT")).ShouldBe(0);
        (await memoryGraph.ListAllAsync(Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AmbiguousBackfill_FailsThenRetriesExistingCatalogLabels()
    {
        await Db.GetService<IMigrator>().MigrateAsync(PreviousMigration, Ct);
        var first = await AddGroupAsync("duplicate", "duplicate");
        var second = await AddGroupAsync("duplicate");
        PostgresException error = await Should.ThrowAsync<PostgresException>(() => Db.GetService<IMigrator>().MigrateAsync(cancellationToken: Ct));
        error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        Db.MemoryGroups.Remove(second);
        await Db.SaveChangesAsync(Ct);
        await Db.GetService<IMigrator>().MigrateAsync(cancellationToken: Ct);
        (await CountAsync("Ticket")).ShouldBe(1);
        (await CountAsync("TICKET_PARENT")).ShouldBe(0);
        (await Db.MemoryGroups.AsNoTracking().SingleAsync(Ct)).Id.ShouldBe(first.Id);
    }

    private async Task<MemoryGroup> AddGroupAsync(params string[] keys)
    {
        var group = TestEntities.NewGroup(tickets: keys.Select(Ticket).ToList());
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        return group;
    }

    private Task<bool> DeclareAsync(string child, string parent) => Graph.ChangeParentAsync(
        new TicketParentChange(new("jira", child), new("jira", parent), null, "declared", "practitioner"), Ct);

    private static TicketDocument Ticket(string key) => TicketDocument.Create("jira", key, "https://tracker/ticket");

    private async Task<long> CountAsync(string label)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand($"SELECT count(*) FROM memory_graph.\"{label}\"");
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
    }

    private async Task<bool> InsertAsync(SmoothAiProductContextMemoryDbContext context)
    {
        try { await context.SaveChangesAsync(Ct); return true; }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }
}
