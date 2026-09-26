using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Features.Groups;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

public sealed class AppendGroupDescriptionHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Appends_description_and_increments_version()
    {
        MemoryGroup group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        AppendGroupDescription.Response first = await NewHandler().Handle(
            new AppendGroupDescription.Request(group.Uuid, "v1", "first body"), Ct);
        AppendGroupDescription.Response second = await NewHandler().Handle(
            new AppendGroupDescription.Request(group.Uuid, "v2", "second body"), Ct);

        first.Version.ShouldBe(1);
        second.Version.ShouldBe(2);
        (await Db.GroupDescriptions.AsNoTracking().CountAsync(d => d.GroupId == group.Id, Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Unknown_group_throws_not_found()
    {
        await Should.ThrowAsync<NotFoundException>(async () =>
            await NewHandler().Handle(new AppendGroupDescription.Request(Guid.NewGuid(), "n", "b"), Ct));
    }

    private AppendGroupDescription.Handler NewHandler() =>
        new(AppDb, ErrorMapper, Loggers.CreateLogger<AppendGroupDescription.Handler>());
}
