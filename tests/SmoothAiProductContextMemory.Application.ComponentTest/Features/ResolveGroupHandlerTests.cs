using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.Groups;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

public sealed class ResolveGroupHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Reresolve_inspects_all_tickets_but_does_not_merge_unowned_tickets()
    {
        MemoryGroup group = TestEntities.NewGroup(tickets: [TicketDocument.Create("jira", "APP-1", "")]);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        ResolveGroup.Response response = await NewHandler().Handle(Request("NEW-1", "APP-1", "NEW-2"), Ct);

        response.Created.ShouldBeFalse();
        response.Uuid.ShouldBe(group.Uuid);
        response.Tickets.Select(t => t.Key).ShouldBe(["APP-1"]);
        (await Db.MemoryGroups.AsNoTracking().SingleAsync(g => g.Uuid == group.Uuid, Ct))
            .Tickets.Select(t => t.Key).ShouldBe(["APP-1"]);
        Db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Theory]
    [InlineData("APP-1", "APP-2")]
    [InlineData("APP-2", "APP-1")]
    public async Task Tickets_owned_by_different_groups_conflict_regardless_of_order(string first, string second)
    {
        Db.MemoryGroups.AddRange(
            TestEntities.NewGroup(tickets: [TicketDocument.Create("jira", "APP-1", "")]),
            TestEntities.NewGroup(tickets: [TicketDocument.Create("jira", "APP-2", "")]));
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        await Should.ThrowAsync<ConflictException>(async () => await NewHandler().Handle(Request(first, second), Ct));

        Db.Database.CurrentTransaction.ShouldBeNull();
        (await Db.MemoryGroups.CountAsync(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task New_group_commits_and_can_be_resolved_again()
    {
        ResolveGroup.Response created = await NewHandler().Handle(Request("APP-1", "APP-2"), Ct);
        created.Created.ShouldBeTrue();
        Db.Database.CurrentTransaction.ShouldBeNull();
        Db.ChangeTracker.Clear();

        ResolveGroup.Response existing = await NewHandler().Handle(Request("APP-2"), Ct);
        existing.Created.ShouldBeFalse();
        existing.Uuid.ShouldBe(created.Uuid);
        existing.Tickets.Select(t => t.Key).ShouldBe(["APP-1", "APP-2"]);
    }

    private ResolveGroup.Handler NewHandler() =>
        new(AppDb, ErrorMapper, new NpgsqlTicketGraph(Db), Loggers.CreateLogger<ResolveGroup.Handler>());

    private static ResolveGroup.Request Request(params string[] keys) =>
        new([.. keys.Select(key => new TicketInput("jira", key, ""))], null, null, null, null, null, null, null);
}
