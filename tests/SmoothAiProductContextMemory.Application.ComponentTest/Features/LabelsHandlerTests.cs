using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Features.Labels;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

public sealed class LabelsHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Propose_label_creates_a_draft_and_is_idempotent()
    {
        ProposeLabel.Response created = await NewProposeHandler().Handle(new ProposeLabel.Request("steady-context"), Ct);
        created.Status.ShouldBe(Label.LabelStatus.Draft);

        ProposeLabel.Response again = await NewProposeHandler().Handle(new ProposeLabel.Request("steady-context"), Ct);
        again.Status.ShouldBe(Label.LabelStatus.Draft);

        (await Db.Labels.AsNoTracking().CountAsync(l => l.Name == "steady-context", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Get_labels_unions_registry_with_usage()
    {
        MemoryGroup group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        Memory memory = TestEntities.NewMemory(group.Id, "m", "a fact to remember");
        memory.Facets = ["in-use-facet", "shared-facet"];
        Db.Memories.Add(memory);
        Db.Labels.Add(new Label { Name = "registered-only", Status = Label.LabelStatus.Draft });
        Db.Labels.Add(new Label { Name = "shared-facet", Status = Label.LabelStatus.Active });
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        GetLabels.Response response = await NewListHandler().Handle(new GetLabels.Request(), Ct);

        GetLabels.LabelRow shared = response.Items.Single(r => r.Name == "shared-facet");
        shared.Status.ShouldBe(Label.LabelStatus.Active);
        shared.Uses.ShouldBe(1);

        GetLabels.LabelRow used = response.Items.Single(r => r.Name == "in-use-facet");
        used.Status.ShouldBeNull();
        used.Uses.ShouldBe(1);

        response.Items.Select(r => r.Name).ShouldContain("registered-only");
    }

    private ProposeLabel.Handler NewProposeHandler() =>
        new(AppDb, ErrorMapper, Loggers.CreateLogger<ProposeLabel.Handler>());

    private GetLabels.Handler NewListHandler() =>
        new(AppDb, Loggers.CreateLogger<GetLabels.Handler>());
}
