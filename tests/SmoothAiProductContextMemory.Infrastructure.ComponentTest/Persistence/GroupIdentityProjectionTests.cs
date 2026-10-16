using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// L1 — the read projection carries the group's repo and its resolved initiative name, so a card can
/// show the binding the query filtered on without a second round trip. It runs against the real
/// provider because the initiative name is a correlated subquery, which only proves itself when the
/// SQL actually translates.
/// </summary>
public sealed class GroupIdentityProjectionTests : PersistenceTestBase
{
    public GroupIdentityProjectionTests(AspireFixture aspire) : base(aspire) { }

    private NpgsqlMemorySearch Search => new(Db);

    private async Task<(MemoryGroup Group, Memory Memory)> SeedBoundAsync(string? repo)
    {
        var initiative = new Initiative { Name = $"initiative-{Guid.NewGuid():N}", Description = "projection fixture" };
        Db.Initiatives.Add(initiative);
        await Db.SaveChangesAsync(Ct);

        var group = TestEntities.NewGroup(repo: repo, initiativeId: initiative.Id);
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
    public async Task The_repo_and_the_initiative_name_ride_along_with_the_row()
    {
        var (group, _) = await SeedBoundAsync("smooth-ai-product-context-memory");

        var row = (await Search.SearchAsync(new MemorySearchCriteria { Limit = 10 }, Ct))
            .ShouldHaveSingleItem();

        row.GroupUuid.ShouldBe(group.Uuid);
        row.Repository.ShouldBe("smooth-ai-product-context-memory");
        var expected = await Db.Initiatives.Where(i => i.Id == group.InitiativeId).Select(i => i.Name).SingleAsync(Ct);
        row.InitiativeName.ShouldBe(expected);
    }

    [Fact]
    public async Task An_unbound_group_projects_a_null_repo_rather_than_failing_the_row()
    {
        var group = TestEntities.NewGroup(repo: null);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        var memory = TestEntities.NewMemory(group.Id, "Unbound memory", "A memory whose group carries nothing");
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);
        Db.MemoryVersions.Add(TestEntities.NewVersion(memory.Id, 1, "The claim"));
        await Db.SaveChangesAsync(Ct);

        var row = (await Search.SearchAsync(new MemorySearchCriteria { Limit = 10 }, Ct))
            .ShouldHaveSingleItem();

        row.Repository.ShouldBeNull();
    }

    /// <summary>
    /// The projection must not smuggle the group's stored ticket documents onto every row. Tickets are
    /// the one identity with two homes (the membership array and the graph's ABOUT edges), so they are
    /// assembled once per page by <c>TicketIdentityLookup</c>; a row that still carried them would mean
    /// the whole array shipped on every row of the page.
    /// </summary>
    [Fact]
    public async Task The_projection_leaves_tickets_to_the_page_level_assembly()
    {
        var group = TestEntities.NewGroup(
            tickets: [TicketDocument.Create("github", "262", "https://github.com/org/repo/issues/262")]);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        var memory = TestEntities.NewMemory(group.Id, "Ticketed memory", "A memory whose group owns a ticket");
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);
        Db.MemoryVersions.Add(TestEntities.NewVersion(memory.Id, 1, "The claim"));
        await Db.SaveChangesAsync(Ct);

        var row = (await Search.SearchAsync(new MemorySearchCriteria { Limit = 10 }, Ct))
            .ShouldHaveSingleItem();

        row.Tickets.ShouldBeEmpty();
    }

    /// <summary>
    /// A repo-narrowed query must still return the repo it narrowed on, or the UI's autofill and the
    /// lineage column have nothing to show.
    /// </summary>
    [Fact]
    public async Task A_repo_narrowed_query_still_carries_the_repo_it_narrowed_on()
    {
        await SeedBoundAsync("smooth-ai-product-context-memory");

        var row = (await Search.SearchAsync(
            new MemorySearchCriteria { Repo = "smooth-ai-product-context-memory", Limit = 10 }, Ct))
            .ShouldHaveSingleItem();

        row.Repository.ShouldBe("smooth-ai-product-context-memory");
    }
}
