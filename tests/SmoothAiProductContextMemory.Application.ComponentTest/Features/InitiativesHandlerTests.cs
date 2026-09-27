using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Features.Initiatives;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

public sealed class InitiativesHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Get_returns_created_initiatives_ordered_by_name()
    {
        Db.Initiatives.AddRange(
            new Initiative { Name = "zeta", Description = "last", Status = Initiative.InitiativeStatus.Archived },
            new Initiative { Name = "alpha", Description = "first", Status = Initiative.InitiativeStatus.Archived });
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        GetInitiatives.Response response = await NewListHandler().Handle(new GetInitiatives.Request("archived"), Ct);

        response.Items.Select(i => i.Name).ShouldBe(["alpha", "zeta"]);
    }

    [Fact]
    public async Task Get_filters_by_status()
    {
        Db.Initiatives.AddRange(
            new Initiative { Name = "live", Description = "active", Status = Initiative.InitiativeStatus.Active },
            new Initiative { Name = "done", Description = "archived", Status = Initiative.InitiativeStatus.Archived });
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        GetInitiatives.Response archived = await NewListHandler().Handle(new GetInitiatives.Request("archived"), Ct);
        archived.Items.Select(i => i.Name).ShouldBe(["done"]);

        GetInitiatives.Response active = await NewListHandler().Handle(new GetInitiatives.Request("active"), Ct);
        active.Items.Select(i => i.Name).ShouldContain("live");
    }

    [Fact]
    public async Task Get_returns_an_archive_filter_with_no_matches_as_empty()
    {
        // The migration seeds a default "active" initiative; an archived filter must not see it.
        GetInitiatives.Response response = await NewListHandler().Handle(new GetInitiatives.Request("archived"), Ct);

        response.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upsert_creates_a_new_initiative()
    {
        UpsertInitiative.Response response = await NewUpsertHandler().Handle(
            new UpsertInitiative.Request("wt", "work tree", "active"), Ct);

        response.Created.ShouldBeTrue();
        (await Db.Initiatives.AsNoTracking().SingleAsync(i => i.Name == "wt", Ct))
            .Description.ShouldBe("work tree");
    }

    [Fact]
    public async Task Upsert_updates_existing_without_duplicating()
    {
        Db.Initiatives.Add(new Initiative { Name = "wt", Description = "old", Status = Initiative.InitiativeStatus.Active });
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        UpsertInitiative.Response response = await NewUpsertHandler().Handle(
            new UpsertInitiative.Request("wt", Description: "new"), Ct);

        response.Created.ShouldBeFalse();
        response.Description.ShouldBe("new");
        (await Db.Initiatives.AsNoTracking().CountAsync(i => i.Name == "wt", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Upsert_archives_with_status_only()
    {
        UpsertInitiative.Response response = await NewUpsertHandler().Handle(
            new UpsertInitiative.Request("wt", Status: Initiative.InitiativeStatus.Archived), Ct);

        response.Status.ShouldBe(Initiative.InitiativeStatus.Archived);
    }

    private GetInitiatives.Handler NewListHandler() =>
        new(AppDb, Loggers.CreateLogger<GetInitiatives.Handler>());

    private UpsertInitiative.Handler NewUpsertHandler() =>
        new(AppDb, ErrorMapper, Loggers.CreateLogger<UpsertInitiative.Handler>());
}
