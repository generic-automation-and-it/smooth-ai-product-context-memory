using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

public sealed class QueryMemoriesHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Open_search_omits_program_and_proposed()
    {
        var product = TestEntities.NewGroup();
        var program = TestEntities.NewGroup(MemoryGroup.ScopeDimensionValue.Program);
        Db.MemoryGroups.AddRange(product, program);
        await Db.SaveChangesAsync(Ct);

        var set = new SetMemories.Handler(AppDb, Blob, Loggers.CreateLogger<SetMemories.Handler>());
        await set.Handle(Approved(product.Uuid, "Product fact"), Ct);
        await set.Handle(Proposed(product.Uuid, "Proposed fact"), Ct);
        await set.Handle(Approved(program.Uuid, "Program fact"), Ct);

        var query = new QueryMemories.Handler(AppDb, Loggers.CreateLogger<QueryMemories.Handler>());
        QueryMemories.Response response = await query.Handle(new QueryMemories.Request(
            null, null, null, null, null, null, null, null, null, null, null), Ct);

        response.Items.Select(i => i.Description).ShouldBe(["Product fact"]);
        response.Items[0].ScopeDimension.ShouldBe(MemoryGroup.ScopeDimensionValue.Product);
    }

    private static SetMemories.Request Approved(Guid group, string description) =>
        Write(group, description, MemoryVersion.MemoryVersionStatus.Approved);

    private static SetMemories.Request Proposed(Guid group, string description) =>
        Write(group, description, MemoryVersion.MemoryVersionStatus.Proposed);

    private static SetMemories.Request Write(Guid group, string description, string status) =>
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
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null)
            ],
            null,
            null);
}
