using Microsoft.Extensions.Logging;
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

        var set = new SetMemories.Handler(AppDb, Blob, ErrorMapper, Loggers.CreateLogger<SetMemories.Handler>());
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
}
