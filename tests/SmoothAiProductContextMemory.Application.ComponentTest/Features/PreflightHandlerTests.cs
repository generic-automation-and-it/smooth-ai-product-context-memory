using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Application.Features.Preflight;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

public sealed class PreflightHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Cross_group_slug_match_and_intra_batch_collision()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var set = new SetMemories.Handler(AppDb, Graph, Blob, ErrorMapper, Loggers.CreateLogger<SetMemories.Handler>());
        await set.Handle(
            new SetMemories.Request(
                group.Uuid,
                [
                    new SetMemories.MemoryWrite(
                        null,
                        "Name",
                        "The storage engine",
                        "We use Postgres",
                        "Summary",
                        MemoryVersion.KindValue.Architecture,
                        ["storage"],
                        null,
                        MemoryVersion.MemoryVersionStatus.Approved,
                        80,
                        null,
                        null,
                        DateTimeOffset.UtcNow,
                        null,
                        null,
                        null)
                ],
                null,
                null),
            Ct);

        var handler = new Preflight.Handler(AppDb, Loggers.CreateLogger<Preflight.Handler>());
        Preflight.Response response = await handler.Handle(
            new Preflight.Request(
            [
                new Preflight.Candidate("The storage engine", MemoryVersion.KindValue.Architecture, ["storage"], null),
                new Preflight.Candidate("The storage engine", null, null, null),
            ]),
            Ct);

        response.Candidates[0].Matches.ShouldNotBeEmpty();
        response.IntraBatchCollisions.Count.ShouldBe(1);
    }

    /// <summary>
    /// Ticket uniqueness is about ownership by <em>another</em> group. Reporting the group the caller
    /// is writing into made every candidate of a real batch a conflict, which is noise the caller
    /// cannot act on.
    /// </summary>
    [Fact]
    public async Task Ticket_owned_by_the_declared_group_is_not_a_conflict()
    {
        string key = $"preflight-self-{Guid.NewGuid():N}";
        MemoryGroup owner = TestEntities.NewGroup(tickets: [TicketDocument.Create("local", key, string.Empty)]);
        Db.MemoryGroups.Add(owner);
        await Db.SaveChangesAsync(Ct);

        var handler = new Preflight.Handler(AppDb, Loggers.CreateLogger<Preflight.Handler>());
        var ticket = new TicketInput("local", key, string.Empty);

        Preflight.Response declared = await handler.Handle(
            new Preflight.Request([new Preflight.Candidate("A subject", Ticket: ticket, GroupUuid: owner.Uuid)]),
            Ct);
        declared.Candidates[0].TicketConflict.ShouldBeNull();

        Preflight.Response otherGroup = await handler.Handle(
            new Preflight.Request([new Preflight.Candidate("A subject", Ticket: ticket, GroupUuid: Guid.NewGuid())]),
            Ct);
        otherGroup.Candidates[0].TicketConflict!.GroupUuid.ShouldBe(owner.Uuid);

        // No declared group means the caller has not said which ownership is its own, so every owner
        // is reported rather than silently suppressed.
        Preflight.Response undeclared = await handler.Handle(
            new Preflight.Request([new Preflight.Candidate("A subject", Ticket: ticket)]),
            Ct);
        undeclared.Candidates[0].TicketConflict!.GroupUuid.ShouldBe(owner.Uuid);
    }
}
