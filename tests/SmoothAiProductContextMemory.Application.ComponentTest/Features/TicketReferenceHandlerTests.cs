using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using Shouldly;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

/// <summary>
/// The <c>ABOUT</c> memory→ticket reference write path (HLD-003 LADR-09): request-level references on
/// <see cref="SetMemories"/>, the standalone batch, and the two invariants the migration enforces —
/// a referenced ticket cannot lose its last owner, and a deleted memory takes its references with it.
/// </summary>
public sealed class TicketReferenceHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    private const string Provider = "github";

    [Fact]
    public async Task Set_with_a_ticket_reference_creates_one_about_edge_in_the_write_transaction()
    {
        (_, MemoryGroup target, string key) = await ArrangeAsync();
        Guid createUuid = Guid.NewGuid();

        SetMemories.Response response = await NewSetHandler().Handle(
            Write(target.Uuid, "Reference subject", "Claim", createUuid: createUuid) with
            {
                TicketReferences = [new TicketReferenceInput(Provider, key, "WET copy across initiatives")],
            },
            Ct);

        response.Created.ShouldBe(1);
        response.TicketReferencesCreated.ShouldBe(1);
        response.TicketReferencesAlreadyPresent.ShouldBe(0);
        response.Items[0].Uuid.ShouldBe(createUuid);

        (await Graph.AboutExistsAsync(createUuid, Provider, key, Ct)).ShouldBeTrue();
        (await AboutCountAsync(Provider, key)).ShouldBe(1);
        (await AboutReasonAsync(createUuid, Provider, key)).ShouldBe("WET copy across initiatives");
    }

    [Fact]
    public async Task Set_with_a_ticket_reference_applies_to_every_memory_in_the_batch()
    {
        (_, MemoryGroup target, string key) = await ArrangeAsync();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        SetMemories.Request request = Write(target.Uuid, "First subject", "Claim", createUuid: first) with
        {
            Items =
            [
                Write(target.Uuid, "First subject", "Claim", createUuid: first).Items[0],
                Write(target.Uuid, "Second subject", "Claim", createUuid: second).Items[0],
            ],
            TicketReferences = [new TicketReferenceInput(Provider, key)],
        };

        SetMemories.Response response = await NewSetHandler().Handle(request, Ct);

        response.Created.ShouldBe(2);
        response.TicketReferencesCreated.ShouldBe(2);
        (await AboutCountAsync(Provider, key)).ShouldBe(2);
        (await Graph.AboutExistsAsync(first, Provider, key, Ct)).ShouldBeTrue();
        (await Graph.AboutExistsAsync(second, Provider, key, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Dry_run_reports_the_references_and_writes_no_edge()
    {
        (_, MemoryGroup target, string key) = await ArrangeAsync();
        Guid createUuid = Guid.NewGuid();

        SetMemories.Response predicted = await NewSetHandler().Handle(
            Write(target.Uuid, "Reference subject", "Claim", createUuid: createUuid) with
            {
                TicketReferences = [new TicketReferenceInput(Provider, key)],
                DryRun = true,
            },
            Ct);

        predicted.TicketReferencesCreated.ShouldBe(1);
        (await AboutCountAsync(Provider, key)).ShouldBe(0);
        (await Graph.AboutExistsAsync(createUuid, Provider, key, Ct)).ShouldBeFalse();
        (await Db.Memories.CountAsync(m => m.GroupId == target.Id, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Referencing_an_unowned_ticket_is_refused_and_writes_nothing()
    {
        MemoryGroup target = TestEntities.NewGroup();
        Db.MemoryGroups.Add(target);
        await Db.SaveChangesAsync(Ct);
        string orphan = Key("ORPHAN");

        // The reference is resolved during planning, before the transaction opens, so the refusal
        // leaves no memory behind either — the whole request is refused, not partially applied.
        await Should.ThrowAsync<NotFoundException>(() => NewSetHandler().Handle(
            Write(target.Uuid, "Reference subject", "Claim") with
            {
                TicketReferences = [new TicketReferenceInput(Provider, orphan)],
            },
            Ct).AsTask());

        (await AboutCountAsync(Provider, orphan)).ShouldBe(0);
        (await Db.Memories.CountAsync(m => m.GroupId == target.Id, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Referencing_a_ticket_the_writing_group_already_owns_is_refused()
    {
        string key = Key("OWN");
        MemoryGroup owner = TestEntities.NewGroup(tickets: [TicketDocument.Create(Provider, key, string.Empty)]);
        Db.MemoryGroups.Add(owner);
        await Db.SaveChangesAsync(Ct);

        await Should.ThrowAsync<ConflictException>(() => NewSetHandler().Handle(
            Write(owner.Uuid, "Reference subject", "Claim") with
            {
                TicketReferences = [new TicketReferenceInput(Provider, key)],
            },
            Ct).AsTask());

        (await AboutCountAsync(Provider, key)).ShouldBe(0);
        (await Db.Memories.CountAsync(m => m.GroupId == owner.Id, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Releasing_the_last_owner_of_a_referenced_ticket_is_refused()
    {
        (MemoryGroup owner, MemoryGroup target, string key) = await ArrangeAsync();
        await NewSetHandler().Handle(
            Write(target.Uuid, "Reference subject", "Claim") with
            {
                TicketReferences = [new TicketReferenceInput(Provider, key)],
            },
            Ct);
        (await AboutCountAsync(Provider, key)).ShouldBe(1);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(() => ReleaseTicketsAsync(owner.Id));
        ex.SqlState.ShouldBe("23505", ex.Message);

        // The reference survived the refused release — that is the point of the guard.
        (await AboutCountAsync(Provider, key)).ShouldBe(1);
    }

    [Fact]
    public async Task Releasing_an_unreferenced_ticket_still_removes_its_vertex()
    {
        (MemoryGroup owner, _, string key) = await ArrangeAsync();
        (await AboutCountAsync(Provider, key)).ShouldBe(0);
        (await TicketVertexExistsAsync(Provider, key)).ShouldBeTrue();

        await ReleaseTicketsAsync(owner.Id);

        (await TicketVertexExistsAsync(Provider, key)).ShouldBeFalse();
    }

    [Fact]
    public async Task Deleting_a_memory_removes_its_about_edges()
    {
        (_, MemoryGroup target, string key) = await ArrangeAsync();

        // A versionless memory, so the delete exercises the graph cascade rather than the append-only
        // history guard — the same shape LinkTests uses for the LINKS cascade.
        Memory memory = TestEntities.NewMemory(target.Id, "Name", "Reference subject");
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);
        (await Graph.CreateAboutAsync(memory.Uuid, Provider, key, "reason", Ct)).ShouldBeTrue();
        (await AboutCountAsync(Provider, key)).ShouldBe(1);

        Db.Memories.Remove(memory);
        await Db.SaveChangesAsync(Ct);

        (await AboutCountAsync(Provider, key)).ShouldBe(0);
        // The referenced ticket keeps its vertex: only the memory's own edges cascade.
        (await TicketVertexExistsAsync(Provider, key)).ShouldBeTrue();
    }

    [Fact]
    public async Task Standalone_batch_creates_references_and_is_idempotent()
    {
        (_, MemoryGroup target, string key) = await ArrangeAsync();
        SetMemories.Response created = await NewSetHandler().Handle(
            Write(target.Uuid, "Reference subject", "Claim"), Ct);
        Guid uuid = created.Items[0].Uuid.ShouldNotBeNull();

        AddTicketReferences.Handler handler = NewReferenceHandler();
        var request = new AddTicketReferences.Request(
            [uuid],
            [new TicketReferenceInput(Provider, key, "per-ticket reason")],
            "request-level reason");

        AddTicketReferences.Response first = await handler.Handle(request, Ct);
        first.Created.ShouldBe(1);
        first.AlreadyPresent.ShouldBe(0);

        AddTicketReferences.Response second = await handler.Handle(request, Ct);
        second.Created.ShouldBe(0);
        second.AlreadyPresent.ShouldBe(1);

        (await AboutCountAsync(Provider, key)).ShouldBe(1);
        // The per-ticket reason wins over the request-level one.
        (await AboutReasonAsync(uuid, Provider, key)).ShouldBe("per-ticket reason");
    }

    [Fact]
    public async Task Standalone_batch_falls_back_to_the_request_reason()
    {
        (_, MemoryGroup target, string key) = await ArrangeAsync();
        Guid uuid = (await NewSetHandler().Handle(Write(target.Uuid, "Reference subject", "Claim"), Ct))
            .Items[0].Uuid.ShouldNotBeNull();

        await NewReferenceHandler().Handle(
            new AddTicketReferences.Request([uuid], [new TicketReferenceInput(Provider, key)], "request-level reason"),
            Ct);

        (await AboutReasonAsync(uuid, Provider, key)).ShouldBe("request-level reason");
    }

    [Fact]
    public async Task Standalone_batch_refused_midway_writes_no_edge_at_all()
    {
        (_, MemoryGroup target, string key) = await ArrangeAsync();
        Guid uuid = (await NewSetHandler().Handle(Write(target.Uuid, "Reference subject", "Claim"), Ct))
            .Items[0].Uuid.ShouldNotBeNull();
        string ownKey = Key("OWN");
        // The write handler refreshes tracked state; the earlier arrangement is now detached.
        target = await Db.MemoryGroups.SingleAsync(group => group.Uuid == target.Uuid, Ct);
        target.Tickets = [TicketDocument.Create(Provider, ownKey, string.Empty)];
        await Db.SaveChangesAsync(Ct);

        // One valid reference plus one the memory's own group owns: the whole batch is refused, so the
        // valid reference must not survive as a partial write.
        await Should.ThrowAsync<ConflictException>(() => NewReferenceHandler().Handle(
            new AddTicketReferences.Request(
                [uuid],
                [new TicketReferenceInput(Provider, key), new TicketReferenceInput(Provider, ownKey)],
                "reason"),
            Ct).AsTask());

        (await AboutCountAsync(Provider, key)).ShouldBe(0);
        (await AboutCountAsync(Provider, ownKey)).ShouldBe(0);
    }

    [Fact]
    public async Task Standalone_batch_counts_a_repeated_ticket_once()
    {
        (_, MemoryGroup target, string key) = await ArrangeAsync();
        Guid uuid = (await NewSetHandler().Handle(Write(target.Uuid, "Reference subject", "Claim"), Ct))
            .Items[0].Uuid.ShouldNotBeNull();

        AddTicketReferences.Response response = await NewReferenceHandler().Handle(
            new AddTicketReferences.Request(
                [uuid],
                [new TicketReferenceInput(Provider, key), new TicketReferenceInput(Provider, key)],
                "reason"),
            Ct);

        response.Created.ShouldBe(1);
        response.AlreadyPresent.ShouldBe(0);
        (await AboutCountAsync(Provider, key)).ShouldBe(1);
    }

    [Fact]
    public async Task Standalone_batch_for_an_unknown_memory_is_refused()
    {
        (_, _, string key) = await ArrangeAsync();

        await Should.ThrowAsync<NotFoundException>(() => NewReferenceHandler().Handle(
            new AddTicketReferences.Request([Guid.NewGuid()], [new TicketReferenceInput(Provider, key)], "reason"),
            Ct).AsTask());

        (await AboutCountAsync(Provider, key)).ShouldBe(0);
    }

    private async Task<(MemoryGroup Owner, MemoryGroup Target, string Key)> ArrangeAsync()
    {
        string key = Key("REF");
        MemoryGroup owner = TestEntities.NewGroup(tickets: [TicketDocument.Create(Provider, key, string.Empty)]);
        MemoryGroup target = TestEntities.NewGroup();
        Db.MemoryGroups.AddRange(owner, target);
        await Db.SaveChangesAsync(Ct);
        return (owner, target, key);
    }

    private static string Key(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private SetMemories.Handler NewSetHandler() =>
        new(AppDb, Graph, Blob, ErrorMapper, ProductGraph, Loggers.CreateLogger<SetMemories.Handler>(), new SmoothAiProductContextMemory.Infrastructure.Persistence.NpgsqlCorpusCommitStore(Db));

    private AddTicketReferences.Handler NewReferenceHandler() =>
        new(AppDb, Graph, Loggers.CreateLogger<AddTicketReferences.Handler>());

    private static SetMemories.Request Write(
        Guid groupUuid,
        string description,
        string statement,
        Guid? uuid = null,
        Guid? createUuid = null) =>
        new(
            groupUuid,
            [
                new SetMemories.MemoryWrite(
                    uuid,
                    "Name",
                    description,
                    statement,
                    "Summary",
                    MemoryVersion.KindValue.Decision,
                    ["architecture"],
                    ["tag"],
                    MemoryVersion.MemoryVersionStatus.Approved,
                    80,
                    "blob-body",
                    null,
                    SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromDays(-1)),
                    null,
                    "test-model",
                    "prompt-1",
                    createUuid)
            ],
            null,
            null);

    /// <summary>
    /// Drops every ticket from a group as raw SQL, so the membership trigger — and with it the D1
    /// guard — runs exactly as it does for any other ownership change, with no EF change tracker
    /// between the test and the trigger's exception.
    /// </summary>
    private async Task ReleaseTicketsAsync(long groupId) =>
        await Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE memory_group SET tickets = '[]'::jsonb WHERE id = {groupId}",
            Ct);

    private async Task<long> AboutCountAsync(string provider, string key)
    {
        object? result = await ScalarAsync(
            "SELECT public.ticket_graph_about_count(@provider, @key)",
            ("provider", provider),
            ("key", key));
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private async Task<bool> TicketVertexExistsAsync(string provider, string key)
    {
        // cypher() takes its whole query as one literal, so identities are interpolated with the same
        // escaping the production graph writer uses rather than bound as parameters.
        string sql =
            "SELECT count(*) FROM ag_catalog.cypher('memory_graph', "
            + $"$q$MATCH (v:Ticket) WHERE v.provider = {Quote(provider)} AND v.key = {Quote(key)} RETURN v$q$"
            + ") AS (v ag_catalog.agtype)";
        object? result = await ScalarAsync(sql);
        return result is not null and not DBNull && Convert.ToInt64(result) > 0;
    }

    private async Task<string?> AboutReasonAsync(Guid memoryUuid, string provider, string key)
    {
        // The uuid is formatted, never concatenated from caller input, so it carries no quote or tag.
        string sql =
            "SELECT v::text FROM ag_catalog.cypher('memory_graph', "
            + $"$q$MATCH (s:Memory {{memory_uuid: '{memoryUuid:D}'}})-[e:ABOUT]->(t:Ticket) "
            + $"WHERE t.provider = {Quote(provider)} AND t.key = {Quote(key)} RETURN e.reason$q$"
            + ") AS (v ag_catalog.agtype)";
        object? result = await ScalarAsync(sql);

        // `v::text` over an agtype string yields the bare value, not a JSON-quoted one.
        return result is null or DBNull ? null : result.ToString();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, string Value)[] parameters)
    {
        DbConnection connection = Db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(Ct);
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, string value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return await command.ExecuteScalarAsync(Ct);
    }

    private static string Quote(string value) => $"'{value.Replace("\\", "\\\\").Replace("'", "\\'")}'";
}
