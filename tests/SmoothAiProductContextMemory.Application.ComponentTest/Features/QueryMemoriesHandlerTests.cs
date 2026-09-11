using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

/// <summary>
/// Runs against the real provider search so the predicates are proven as SQL. An in-memory filter
/// would pass while the database did something else.
/// </summary>
public sealed class QueryMemoriesHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Open_search_omits_program_and_proposed()
    {
        var product = TestEntities.NewGroup();
        var program = TestEntities.NewGroup(MemoryGroup.ScopeDimensionValue.Program);
        Db.MemoryGroups.AddRange(product, program);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler set = NewSet();
        await set.Handle(Approved(product.Uuid, "Product fact"), Ct);
        await set.Handle(Proposed(product.Uuid, "Proposed fact"), Ct);
        await set.Handle(Approved(program.Uuid, "Program fact"), Ct);

        QueryMemories.Response response = await NewQuery().Handle(Query(), Ct);

        response.Items.Select(i => i.Description).ShouldBe(["Product fact"]);
        response.Items[0].ScopeDimension.ShouldBe(MemoryGroup.ScopeDimensionValue.Product);
    }

    [Fact]
    public async Task Explicit_program_scope_returns_program()
    {
        var program = TestEntities.NewGroup(MemoryGroup.ScopeDimensionValue.Program);
        Db.MemoryGroups.Add(program);
        await Db.SaveChangesAsync(Ct);

        await NewSet().Handle(Approved(program.Uuid, "Program fact"), Ct);

        QueryMemories.Response response = await NewQuery().Handle(
            Query() with { ScopeDimension = MemoryGroup.ScopeDimensionValue.Program },
            Ct);

        response.Items.Select(i => i.Description).ShouldBe(["Program fact"]);
    }

    [Fact]
    public async Task Full_text_matches_subject_and_claim()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        await NewSet().Handle(Approved(group.Uuid, "The storage engine", "We chose PostgreSQL"), Ct);
        await NewSet().Handle(Approved(group.Uuid, "The queue", "We chose RabbitMQ"), Ct);

        QueryMemories.Handler query = NewQuery();

        (await query.Handle(Query() with { Query = "storage" }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["The storage engine"]);

        // Claim text is part of the search surface, not just the subject.
        (await query.Handle(Query() with { Query = "RabbitMQ" }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["The queue"]);

        (await query.Handle(Query() with { Query = "nothing-matches-this" }, Ct))
            .Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Facets_and_tags_narrow_by_containment()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Request tagged = Approved(group.Uuid, "Tagged fact");
        tagged = tagged with { Items = [tagged.Items[0] with { Facets = ["architecture", "storage"], Tags = ["adr"] }] };
        await NewSet().Handle(tagged, Ct);

        SetMemories.Request other = Approved(group.Uuid, "Other fact");
        other = other with { Items = [other.Items[0] with { Facets = ["process"], Tags = [] }] };
        await NewSet().Handle(other, Ct);

        QueryMemories.Handler query = NewQuery();

        (await query.Handle(Query() with { Facets = ["architecture", "storage"] }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["Tagged fact"]);

        (await query.Handle(Query() with { Tags = ["adr"] }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["Tagged fact"]);

        (await query.Handle(Query() with { Facets = ["architecture", "absent"] }, Ct))
            .Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task As_of_excludes_claims_outside_their_validity()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        SetMemories.Request expired = Approved(group.Uuid, "Expired fact");
        expired = expired with
        {
            Items = [expired.Items[0] with { ValidFrom = now.AddDays(-10), ValidUntil = now.AddDays(-5) }]
        };
        await NewSet().Handle(expired, Ct);

        SetMemories.Request live = Approved(group.Uuid, "Live fact");
        live = live with { Items = [live.Items[0] with { ValidFrom = now.AddDays(-1), ValidUntil = null }] };
        await NewSet().Handle(live, Ct);

        QueryMemories.Handler query = NewQuery();

        (await query.Handle(Query() with { AsOf = now }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["Live fact"]);

        (await query.Handle(Query() with { AsOf = now.AddDays(-7) }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["Expired fact"]);

        // No as-of means no temporal narrowing.
        (await query.Handle(Query(), Ct)).Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Current_only_default_hides_superseded_versions()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler set = NewSet();
        Guid uuid = (await set.Handle(Approved(group.Uuid, "Versioned fact", "Claim 1"), Ct)).Items[0].Uuid!.Value;
        SetMemories.Request bump = Approved(group.Uuid, "Versioned fact", "Claim 2");
        await set.Handle(bump with { Items = [bump.Items[0] with { Uuid = uuid }] }, Ct);

        QueryMemories.Handler query = NewQuery();

        (await query.Handle(Query(), Ct)).Items.Select(i => i.Statement).ShouldBe(["Claim 2"]);
        (await query.Handle(Query() with { CurrentOnly = false }, Ct)).Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Limit_caps_the_result_set()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler set = NewSet();
        for (int i = 0; i < 3; i++)
        {
            await set.Handle(Approved(group.Uuid, $"Fact {i}"), Ct);
        }

        (await NewQuery().Handle(Query() with { Limit = 2 }, Ct)).Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Unknown_ticket_returns_empty_not_an_error()
    {
        QueryMemories.Response response = await NewQuery().Handle(
            Query() with { TicketProvider = "jira", TicketKey = "NOPE-1" },
            Ct);

        response.Items.ShouldBeEmpty();
    }

    private SetMemories.Handler NewSet() =>
        new(AppDb, Blob, ErrorMapper, Loggers.CreateLogger<SetMemories.Handler>());

    private QueryMemories.Handler NewQuery() =>
        new(AppDb, Search, Loggers.CreateLogger<QueryMemories.Handler>());

    private static QueryMemories.Request Query() =>
        new(null, null, null, null, null, null, null, null, null, null, null);

    private static SetMemories.Request Approved(Guid group, string description, string statement = "Claim") =>
        Write(group, description, statement, MemoryVersion.MemoryVersionStatus.Approved);

    private static SetMemories.Request Proposed(Guid group, string description) =>
        Write(group, description, "Claim", MemoryVersion.MemoryVersionStatus.Proposed);

    private static SetMemories.Request Write(Guid group, string description, string statement, string status) =>
        new(
            group,
            [
                new SetMemories.MemoryWrite(
                    null,
                    "Name",
                    description,
                    statement,
                    "Summary",
                    MemoryVersion.KindValue.Decision,
                    null,
                    null,
                    status,
                    80,
                    null,
                    null,
                    DateTimeOffset.UtcNow.AddDays(-1),
                    null,
                    null,
                    null)
            ],
            null,
            null);
}
