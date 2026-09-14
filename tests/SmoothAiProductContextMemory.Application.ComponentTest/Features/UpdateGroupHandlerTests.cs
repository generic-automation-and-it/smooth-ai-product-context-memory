using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.Groups;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

/// <summary>
/// Exercises the ticket-addition path against the real provider, so the JSONB merge and the
/// cross-group ownership lookup are proven as database behaviour rather than in-memory simulation.
/// </summary>
public sealed class UpdateGroupHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Adds_a_ticket_to_an_existing_group()
    {
        MemoryGroup group = TestEntities.NewGroup(tickets:
            [TicketDocument.Create("jira", "ACM-1", "https://example.com/ACM-1")]);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        Guid uuid = group.Uuid;
        UpdateGroup.Response response = await NewHandler().Handle(
            new UpdateGroup.Request(uuid, null, null, null, null, null,
                [new TicketInput("jira", "ACM-2", "https://example.com/ACM-2")]),
            Ct);

        response.Tickets.Select(t => t.Key).ShouldBe(["ACM-1", "ACM-2"]);

        // Assert the persisted row, not the tracked instance: a merge invisible to EF change
        // detection returns a correct-looking response while writing nothing.
        (await ReloadTickets(uuid)).ShouldBe(["ACM-1", "ACM-2"]);
    }

    [Fact]
    public async Task Adding_a_ticket_already_present_is_idempotent()
    {
        MemoryGroup group = TestEntities.NewGroup(tickets:
            [TicketDocument.Create("jira", "ACM-1", "https://example.com/ACM-1")]);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        UpdateGroup.Handler handler = NewHandler();
        var request = new UpdateGroup.Request(group.Uuid, null, null, null, null, null,
            [new TicketInput("jira", "ACM-1", "https://example.com/ACM-1")]);

        UpdateGroup.Response first = await handler.Handle(request, Ct);
        UpdateGroup.Response second = await handler.Handle(request, Ct);

        first.Tickets.Count.ShouldBe(1);
        second.Tickets.Count.ShouldBe(1);
        (await ReloadTickets(group.Uuid)).ShouldBe(["ACM-1"]);
    }

    [Fact]
    public async Task The_same_ticket_twice_in_one_request_merges_once()
    {
        MemoryGroup group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        UpdateGroup.Response response = await NewHandler().Handle(
            new UpdateGroup.Request(group.Uuid, null, null, null, null, null,
                [
                    new TicketInput("jira", "ACM-7", "https://example.com/ACM-7"),
                    new TicketInput("jira", "ACM-7", "https://example.com/ACM-7"),
                ]),
            Ct);

        response.Tickets.Select(t => t.Key).ShouldBe(["ACM-7"]);
        (await ReloadTickets(group.Uuid)).ShouldBe(["ACM-7"]);
    }

    [Fact]
    public async Task Adding_a_ticket_owned_by_another_group_is_rejected()
    {
        MemoryGroup owner = TestEntities.NewGroup(tickets:
            [TicketDocument.Create("jira", "ACM-9", "https://example.com/ACM-9")]);
        MemoryGroup group = TestEntities.NewGroup();
        Db.MemoryGroups.AddRange(owner, group);
        await Db.SaveChangesAsync(Ct);

        var request = new UpdateGroup.Request(group.Uuid, null, null, null, null, null,
            [new TicketInput("jira", "ACM-9", "https://example.com/ACM-9")]);

        var ex = await Should.ThrowAsync<FluentValidation.ValidationException>(
            async () => await NewHandler().Handle(request, Ct));
        ex.Message.ShouldContain("ACM-9");
    }

    private async Task<string[]> ReloadTickets(Guid uuid)
    {
        Db.ChangeTracker.Clear();
        MemoryGroup reloaded = await Db.MemoryGroups.AsNoTracking()
            .SingleAsync(g => g.Uuid == uuid, Ct);
        return [.. reloaded.Tickets.Select(t => t.Key)];
    }

    private UpdateGroup.Handler NewHandler() =>
        new(AppDb, ErrorMapper, Loggers.CreateLogger<UpdateGroup.Handler>());
}
