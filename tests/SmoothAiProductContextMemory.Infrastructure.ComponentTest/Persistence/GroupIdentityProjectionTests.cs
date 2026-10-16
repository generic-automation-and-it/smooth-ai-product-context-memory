using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// L1 — the read model carries the group's identity, so a card can show the ticket / repo /
/// initiative the query filtered on without a second round trip. The ticket list is the whole
/// list: a group accumulates tickets over time, and a projection that kept one would silently
/// hide the rest. These run against the real provider because the projection carries a correlated
/// initiative subquery and a value-converted JSONB column — a change in either shape only shows up
/// when the SQL actually translates.
/// </summary>
public sealed class GroupIdentityProjectionTests : PersistenceTestBase
{
    public GroupIdentityProjectionTests(AspireFixture aspire) : base(aspire) { }

    private NpgsqlMemorySearch Search => new(Db);

    private async Task<(MemoryGroup Group, Memory Memory)> SeedBoundAsync(List<TicketDocument> tickets, string? repo)
    {
        var initiative = new Initiative { Name = $"initiative-{Guid.NewGuid():N}", Description = "projection fixture" };
        Db.Initiatives.Add(initiative);
        await Db.SaveChangesAsync(Ct);

        var group = TestEntities.NewGroup(repo: repo, initiativeId: initiative.Id, tickets: tickets);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var memory = TestEntities.NewMemory(group.Id, "Bound memory", "A memory whose group carries identity");
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);
        Db.MemoryVersions.Add(TestEntities.NewVersion(memory.Id, 1, "The claim"));
        await Db.SaveChangesAsync(Ct);

        return (group, memory);
    }

    [Fact]
    public async Task Every_group_ticket_rides_along_not_just_the_first()
    {
        var (group, _) = await SeedBoundAsync(
        [
            TicketDocument.Create("github", "262", "https://github.com/org/repo/issues/262"),
            TicketDocument.Create("jira", "GPA-234", "https://jira.example/browse/GPA-234"),
        ], repo: "smooth-ai-product-context-memory");

        var row = (await Search.SearchAsync(new MemorySearchCriteria { Limit = 10 }, Ct))
            .ShouldHaveSingleItem();

        row.GroupUuid.ShouldBe(group.Uuid);
        row.Tickets.Select(t => (t.Provider, t.Key)).ShouldBe([("github", "262"), ("jira", "GPA-234")]);
        row.Repository.ShouldBe("smooth-ai-product-context-memory");
    }

    [Fact]
    public async Task The_initiative_name_is_resolved_not_the_group_surrogate_id()
    {
        var (group, _) = await SeedBoundAsync([], repo: null);

        var row = (await Search.SearchAsync(new MemorySearchCriteria { Limit = 10 }, Ct))
            .ShouldHaveSingleItem();

        var expected = await Db.Initiatives.Where(i => i.Id == group.InitiativeId).Select(i => i.Name).SingleAsync(Ct);
        row.InitiativeName.ShouldBe(expected);
    }

    [Fact]
    public async Task An_unbound_group_projects_empty_identity_rather_than_failing_the_row()
    {
        var group = TestEntities.NewGroup(tickets: [], repo: null);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        var memory = TestEntities.NewMemory(group.Id, "Unbound memory", "A memory whose group carries nothing");
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);
        Db.MemoryVersions.Add(TestEntities.NewVersion(memory.Id, 1, "The claim"));
        await Db.SaveChangesAsync(Ct);

        var row = (await Search.SearchAsync(new MemorySearchCriteria { Limit = 10 }, Ct))
            .ShouldHaveSingleItem();

        row.Tickets.ShouldBeEmpty();
        row.Repository.ShouldBeNull();
    }

    /// <summary>
    /// The projection is the reason a card can be rendered from one row: a repo-narrowed query must
    /// still return the repo it narrowed on, or the UI's autofill and lineage have nothing to show.
    /// </summary>
    [Fact]
    public async Task A_repo_narrowed_query_still_carries_the_repo_it_narrowed_on()
    {
        await SeedBoundAsync([TicketDocument.Create("github", "262", "https://github.com/org/repo/issues/262")],
            repo: "smooth-ai-product-context-memory");

        var row = (await Search.SearchAsync(
            new MemorySearchCriteria { Repo = "smooth-ai-product-context-memory", Limit = 10 }, Ct))
            .ShouldHaveSingleItem();

        row.Repository.ShouldBe("smooth-ai-product-context-memory");
        row.Tickets.ShouldHaveSingleItem().Key.ShouldBe("262");
    }
}
