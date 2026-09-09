using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

public sealed class MemoryGroupTests : PersistenceTestBase
{
    public MemoryGroupTests(AspireFixture aspire) : base(aspire) { }

    [Fact]
    public async Task Group_WithTicketsRepoAndInitiative_RoundTrips()
    {
        var tickets = new List<TicketDocument>
        {
            TicketDocument.Create("jira", "ACM-1", "https://example.com/ACM-1"),
            TicketDocument.Create("jira", "ACM-2", "https://example.com/ACM-2"),
        };

        var group = TestEntities.NewGroup(
            scopeDimension: MemoryGroup.ScopeDimensionValue.Customer,
            scopeIdentifier: "customer-1",
            repo: "acme/api",
            repoUrl: "https://github.com/acme/api",
            tickets: tickets);

        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var loaded = await Db.MemoryGroups
            .AsNoTracking()
            .SingleAsync(g => g.Uuid == group.Uuid, Ct);

        loaded.ScopeDimension.ShouldBe(MemoryGroup.ScopeDimensionValue.Customer);
        loaded.ScopeIdentifier.ShouldBe("customer-1");
        loaded.Repo.ShouldBe("acme/api");
        loaded.RepoUrl.ShouldBe("https://github.com/acme/api");
        loaded.InitiativeId.ShouldBe(TestEntities.DefaultInitiativeId);
        loaded.Tickets.Count.ShouldBe(2);
        loaded.Tickets[0].Key.ShouldBe("ACM-1");
        loaded.Tickets[1].Key.ShouldBe("ACM-2");
        loaded.Tickets.All(t => t.V == JsonShapeDocument.CurrentShapeVersion).ShouldBeTrue();
    }

    [Fact]
    public async Task Ticket_Containment_FindsOwningGroup()
    {
        var owned = TestEntities.NewGroup(repo: "acme/api", tickets:
        [
            TicketDocument.Create("jira", "ACM-42", "https://example.com/ACM-42"),
        ]);
        var other = TestEntities.NewGroup(repo: "acme/web", tickets:
        [
            TicketDocument.Create("jira", "WEB-7", "https://example.com/WEB-7"),
        ]);

        Db.MemoryGroups.AddRange(owned, other);
        await Db.SaveChangesAsync(Ct);

        var found = await Db.MemoryGroups
            .Where(g => EF.Functions.JsonContains(g.Tickets, """[{"key":"ACM-42"}]"""))
            .ToListAsync(Ct);

        found.Single().Uuid.ShouldBe(owned.Uuid);
    }

    [Fact]
    public async Task Work_WithNoTicket_IsLegal()
    {
        var group = TestEntities.NewGroup(tickets: []);

        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        (await Db.MemoryGroups.CountAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);
    }
}
