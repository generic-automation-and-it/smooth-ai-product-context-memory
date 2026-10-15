using NodaTime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Application.Features.ProductNames;
using SmoothAiProductContextMemory.Domain.Entities;
using Shouldly;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

/// <summary>
/// Runs the product-name registry against the real graph so the Cypher and the advisory-lock
/// uniqueness are proven, not stubbed (HLD-003 LADR-10).
/// </summary>
public sealed class ProductNameHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Register_link_and_query_both_directions_in_one_hop()
    {
        Initiative alpha = await NewInitiativeAsync("alpha");
        Initiative beta = await NewInitiativeAsync("beta");
        MemoryGroup alphaGroup = await NewGroupWithInitiativeAsync(alpha);
        MemoryGroup betaGroup = await NewGroupWithInitiativeAsync(beta);
        MemoryGroup sharedGroup = await NewGroupWithInitiativeAsync(alpha);
        await WriteApprovedAsync(alphaGroup.Uuid, "Alpha memory");
        await WriteApprovedAsync(betaGroup.Uuid, "Beta memory");
        await WriteApprovedAsync(sharedGroup.Uuid, "Shared alpha memory");

        await RegisterAsync("gmail");
        await RegisterAsync("drive");
        await LinkInitiativeAsync("gmail", "alpha");
        await LinkInitiativeAsync("gmail", "beta");
        await LinkInitiativeAsync("drive", "alpha");

        GetProductLinks.Response gmailLinks = await NewLinks().Handle(
            new GetProductLinks.Request(ProductName: "gmail"), Ct);
        gmailLinks.Items.Select(i => i.InitiativeName).OrderBy(x => x)
            .ShouldBe(new[] { "alpha", "beta" }, ignoreOrder: true);

        GetProductLinks.Response alphaProducts = await NewLinks().Handle(
            new GetProductLinks.Request(InitiativeName: "alpha"), Ct);
        alphaProducts.Items.Select(i => i.ProductName).OrderBy(x => x)
            .ShouldBe(new[] { "drive", "gmail" }, ignoreOrder: true);
    }

    [Fact]
    public async Task Query_by_product_uses_only_that_products_initiatives_and_discloses_shared()
    {
        Initiative alpha = await NewInitiativeAsync("alpha");
        Initiative beta = await NewInitiativeAsync("beta");
        Initiative gamma = await NewInitiativeAsync("gamma");
        MemoryGroup alphaGroup = await NewGroupWithInitiativeAsync(alpha);
        MemoryGroup betaGroup = await NewGroupWithInitiativeAsync(beta);
        MemoryGroup otherAlpha = await NewGroupWithInitiativeAsync(alpha);
        MemoryGroup unrelatedGroup = await NewGroupWithInitiativeAsync(beta);
        MemoryGroup gammaGroup = await NewGroupWithInitiativeAsync(gamma);
        await WriteApprovedAsync(alphaGroup.Uuid, "Alpha memory");
        await WriteApprovedAsync(betaGroup.Uuid, "Beta memory");
        await WriteApprovedAsync(otherAlpha.Uuid, "Other alpha memory");
        await WriteApprovedAsync(unrelatedGroup.Uuid, "Unrelated beta memory");
        await WriteApprovedAsync(gammaGroup.Uuid, "Gamma memory");

        await RegisterAsync("gmail");
        await LinkInitiativeAsync("gmail", "alpha");
        await LinkInitiativeAsync("gmail", "beta");

        // Every memory of gmail's initiatives, none from outside them.
        QueryMemories.Response response = await NewQuery().Handle(
            Query("gmail"), Ct);

        response.Items.Select(i => i.Description).OrderBy(x => x)
            .ShouldBe(new[] { "Alpha memory", "Beta memory", "Other alpha memory", "Unrelated beta memory" }, ignoreOrder: true);
        // gamma is not linked to gmail, so a query that ignored productName and returned everything
        // would fail this assertion — it pins the affinity to the graph resolution.
        response.Items.Select(i => i.Description).ShouldNotContain("Gamma memory");
        // Every returned memory's group initiative (alpha or beta) is linked to gmail, so all match by
        // initiative; neither initiative is shared here (only gmail links them), so none is shared.
        response.Items.ShouldAllBe(i => i.ProductMatch == "initiative");
        response.Items.ShouldAllBe(i => i.SharedInitiative == false);
    }

    [Fact]
    public async Task Query_by_product_marks_shared_initiative_when_two_products_link_it()
    {
        Initiative alpha = await NewInitiativeAsync("alpha");
        MemoryGroup group = await NewGroupWithInitiativeAsync(alpha);
        await WriteApprovedAsync(group.Uuid, "Shared memory");

        await RegisterAsync("gmail");
        await RegisterAsync("drive");
        await LinkInitiativeAsync("gmail", "alpha");
        await LinkInitiativeAsync("drive", "alpha");

        QueryMemories.Response response = await NewQuery().Handle(
            Query("gmail"), Ct);

        response.Items.Single().Description.ShouldBe("Shared memory");
        response.Items.Single().ProductMatch.ShouldBe("initiative");
        response.Items.Single().SharedInitiative.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_product_is_refused_on_link_and_query()
    {
        await Should.ThrowAsync<NotFoundException>(async () =>
            await NewLinks().Handle(new GetProductLinks.Request(ProductName: "nope"), Ct));

        await Should.ThrowAsync<NotFoundException>(async () =>
            await NewLinks().Handle(new GetProductLinks.Request(InitiativeName: "orphan"), Ct));

        await Should.ThrowAsync<NotFoundException>(async () =>
            await NewQuery().Handle(Query("nope"), Ct));

        await Should.ThrowAsync<NotFoundException>(async () =>
            await NewLinkInitiative().Handle(new LinkProductInitiative.Request("nope", "alpha", "reason"), Ct));
    }

    [Fact]
    public async Task Repo_link_dry_run_reports_no_change_for_same_product_and_conflict_for_other()
    {
        await RegisterAsync("gmail");
        await RegisterAsync("drive");
        await LinkRepoAsync("gmail", "acme/widgets");

        // Already linked to this product: dry run predicts no change, and the write is a no-op.
        LinkProductRepo.Response same = await NewLinkRepo().Handle(
            new LinkProductRepo.Request("gmail", "acme/widgets", "reason", DryRun: true), Ct);
        same.Changed.ShouldBeFalse();
        same.ConflictProductName.ShouldBeNull();

        // Another product owns the repo: dry run reports the conflict, write refuses.
        LinkProductRepo.Response conflictDry = await NewLinkRepo().Handle(
            new LinkProductRepo.Request("drive", "acme/widgets", "reason", DryRun: true), Ct);
        conflictDry.Changed.ShouldBeFalse();
        conflictDry.ConflictProductName.ShouldBe("gmail");

        await Should.ThrowAsync<ConflictException>(async () =>
            await LinkRepoAsync("drive", "acme/widgets"));
    }

    [Fact]
    public async Task Set_with_product_name_links_group_initiative_and_dry_run_links_nothing()
    {
        Initiative alpha = await NewInitiativeAsync("alpha");
        MemoryGroup group = await NewGroupWithInitiativeAsync(alpha);
        await RegisterAsync("gmail");

        SetMemories.Response dryRun = await NewSet().Handle(
            Write("gmail", group.Uuid, "Dry run memory", MemoryVersion.MemoryVersionStatus.Approved) with { DryRun = true }, Ct);
        dryRun.ProductLinks.ShouldBe(1);
        (await ProductGraph.GetInitiativeNamesAsync("gmail", Ct)).ShouldBeEmpty();

        SetMemories.Response written = await NewSet().Handle(
            Write("gmail", group.Uuid, "Real memory", MemoryVersion.MemoryVersionStatus.Approved), Ct);
        written.ProductLinks.ShouldBe(1);
        (await ProductGraph.GetInitiativeNamesAsync("gmail", Ct)).ShouldBe(new[] { "alpha" });
    }

    [Fact]
    public async Task Set_with_unregistered_product_refuses_before_writing()
    {
        Initiative alpha = await NewInitiativeAsync("alpha");
        MemoryGroup group = await NewGroupWithInitiativeAsync(alpha);

        await Should.ThrowAsync<NotFoundException>(async () =>
            await NewSet().Handle(Write("nope", group.Uuid, "Should not persist", MemoryVersion.MemoryVersionStatus.Approved), Ct));
        (await Db.Memories.AnyAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Case_folded_and_padded_names_are_refused()
    {
        await RegisterAsync("Gmail");
        await Should.ThrowAsync<ConflictException>(async () => await RegisterAsync("gmail"));

        new UpsertProductName.Validator().Validate(new UpsertProductName.Request("  padded  "))
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task Repo_link_conflict_and_query_repo_match()
    {
        Initiative alpha = await NewInitiativeAsync("alpha");
        MemoryGroup group = await NewGroupWithInitiativeAsync(alpha, repo: "acme/widgets");
        await WriteApprovedAsync(group.Uuid, "Repo memory");

        await RegisterAsync("gmail");
        await RegisterAsync("drive");
        await LinkRepoAsync("gmail", "acme/widgets");

        await Should.ThrowAsync<ConflictException>(() => LinkRepoAsync("drive", "acme/widgets"));

        // A repo-linked group matches through productName even though its initiative is not linked.
        QueryMemories.Response response = await NewQuery().Handle(
            Query("gmail"), Ct);
        response.Items.Single().Description.ShouldBe("Repo memory");
        response.Items.Single().ProductMatch.ShouldBe("repo");
    }

    [Fact]
    public async Task Unlink_removes_exactly_one_edge_and_dry_run_removes_none()
    {
        Initiative alpha = await NewInitiativeAsync("alpha");
        await RegisterAsync("gmail");
        await LinkInitiativeAsync("gmail", "alpha");

        UnlinkProductInitiative.Response dryRun = await NewUnlink().Handle(
            new UnlinkProductInitiative.Request("gmail", "alpha", DryRun: true), Ct);
        dryRun.Changed.ShouldBeTrue();
        (await ProductGraph.GetInitiativeNamesAsync("gmail", Ct)).ShouldBe(new[] { "alpha" });

        UnlinkProductInitiative.Response removed = await NewUnlink().Handle(
            new UnlinkProductInitiative.Request("gmail", "alpha"), Ct);
        removed.Changed.ShouldBeTrue();
        (await ProductGraph.GetInitiativeNamesAsync("gmail", Ct)).ShouldBeEmpty();

        UnlinkProductInitiative.Response again = await NewUnlink().Handle(
            new UnlinkProductInitiative.Request("gmail", "alpha"), Ct);
        again.Changed.ShouldBeFalse();
    }

    private async Task<Initiative> NewInitiativeAsync(string name)
    {
        var initiative = new Initiative { Name = name, Description = name, Status = Initiative.InitiativeStatus.Active };
        Db.Initiatives.Add(initiative);
        await Db.SaveChangesAsync(Ct);
        return initiative;
    }

    private async Task<MemoryGroup> NewGroupWithInitiativeAsync(Initiative initiative, string? repo = null)
    {
        var group = new MemoryGroup { Uuid = Guid.NewGuid(), InitiativeId = initiative.Id, Repo = repo };
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        return group;
    }

    private async Task RegisterAsync(string name)
    {
        UpsertProductName.Response response = await NewUpsert().Handle(
            new UpsertProductName.Request(name), Ct);
        response.Created.ShouldBeTrue();
    }

    private async Task LinkInitiativeAsync(string product, string initiative) =>
        await NewLinkInitiative().Handle(new LinkProductInitiative.Request(product, initiative, "reason"), Ct);

    private async Task LinkRepoAsync(string product, string repo) =>
        await NewLinkRepo().Handle(new LinkProductRepo.Request(product, repo, "reason"), Ct);

    private async Task WriteApprovedAsync(Guid groupUuid, string description)
    {
        SetMemories.Response response = await NewSet().Handle(
            Write(null, groupUuid, description, MemoryVersion.MemoryVersionStatus.Approved), Ct);
        response.Created.ShouldBe(1);
    }

    private SetMemories.Request Write(string? productName, Guid group, string description, string status) =>
        new(
            group,
            [
                new SetMemories.MemoryWrite(
                    null,
                    "Name",
                    description,
                    "Claim",
                    "Summary",
                    MemoryVersion.KindValue.Decision,
                    null,
                    null,
                    status,
                    80,
                    null,
                    null,
                    SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromDays(-1)),
                    null,
                    null,
                    null)
            ],
            null,
            null,
            ProductName: productName);

    private static QueryMemories.Request Query(string? productName) =>
        new(null, null, null, null, null, null, null, null, null, null, null, ProductName: productName);

    private UpsertProductName.Handler NewUpsert() =>
        new(ProductGraph, Loggers.CreateLogger<UpsertProductName.Handler>());

    private GetProductLinks.Handler NewLinks() =>
        new(AppDb, ProductGraph);

    private LinkProductInitiative.Handler NewLinkInitiative() =>
        new(AppDb, ProductGraph, Loggers.CreateLogger<LinkProductInitiative.Handler>());

    private UnlinkProductInitiative.Handler NewUnlink() =>
        new(ProductGraph, Loggers.CreateLogger<UnlinkProductInitiative.Handler>());

    private LinkProductRepo.Handler NewLinkRepo() =>
        new(ProductGraph, Loggers.CreateLogger<LinkProductRepo.Handler>());

    private QueryMemories.Handler NewQuery() =>
        new(AppDb, Search, new NoopRecallFeedback(), ProductGraph, Loggers.CreateLogger<QueryMemories.Handler>());

    private SetMemories.Handler NewSet() =>
        new(AppDb, Graph, Blob, ErrorMapper, ProductGraph, Loggers.CreateLogger<SetMemories.Handler>());

    private sealed class NoopRecallFeedback : IRecallFeedback
    {
        public void Record(RecallFeedbackRecord[] records)
        {
        }
    }
}
