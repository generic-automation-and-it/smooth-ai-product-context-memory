using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

public sealed class SetMemoriesHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Version_bump_flips_old_current_then_inserts()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var handler = new SetMemories.Handler(AppDb, Blob, Loggers.CreateLogger<SetMemories.Handler>());
        SetMemories.Response first = await handler.Handle(Write(group.Uuid, "Subject", "Claim 1"), Ct);
        Guid uuid = first.Items[0].Uuid;

        SetMemories.Response second = await handler.Handle(Write(group.Uuid, "Subject", "Claim 2", uuid), Ct);

        second.Versioned.ShouldBe(1);
        second.Created.ShouldBe(0);

        long memoryId = await Db.Memories.Where(m => m.Uuid == uuid).Select(m => m.Id).SingleAsync(Ct);
        List<MemoryVersion> versions = await Db.MemoryVersions.AsNoTracking()
            .Where(v => v.MemoryId == memoryId)
            .OrderBy(v => v.Version)
            .ToListAsync(Ct);

        versions.Count.ShouldBe(2);
        versions.Count(v => v.IsCurrent).ShouldBe(1);
        versions.Single(v => v.IsCurrent).Version.ShouldBe(2);
        versions.Single(v => v.Version == 1).Statement.ShouldBe("Claim 1");
        versions.Single(v => v.IsCurrent).SummaryStamp.ShouldNotBeNull();
        versions.Single(v => v.IsCurrent).SummaryStamp!.Model.ShouldBe("test-model");
    }

    [Fact]
    public async Task Dry_run_persists_nothing()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var handler = new SetMemories.Handler(AppDb, Blob, Loggers.CreateLogger<SetMemories.Handler>());
        SetMemories.Response dry = await handler.Handle(
            Write(group.Uuid, "Dry subject", "Dry claim") with { DryRun = true },
            Ct);

        dry.Created.ShouldBe(1);
        dry.Items[0].BlobAddress.ShouldBeNull();
        (await Db.Memories.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Jsonb_v_round_trips_on_sources()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var handler = new SetMemories.Handler(AppDb, Blob, Loggers.CreateLogger<SetMemories.Handler>());
        SetMemories.MemoryWrite item = Write(group.Uuid, "Sourced", "Claim").Items[0] with
        {
            Sources = [new("jira", "ACM-1", DateTimeOffset.UtcNow)]
        };
        await handler.Handle(Write(group.Uuid, "Sourced", "Claim") with { Items = [item] }, Ct);

        MemoryVersion version = await Db.MemoryVersions.AsNoTracking().SingleAsync(Ct);
        version.Sources.Count.ShouldBe(1);
        version.Sources[0].V.ShouldBe(JsonShapeDocument.CurrentShapeVersion);
        version.Sources[0].Kind.ShouldBe("jira");
    }

    private static SetMemories.Request Write(Guid groupUuid, string description, string statement, Guid? uuid = null) =>
        new(
            groupUuid,
            [
                new SetMemories.MemoryWrite(
                    uuid,
                    "Name",
                    description,
                    statement,
                    "Summary",
                    MemoryVersion.KindValue.Decision,
                    ["architecture"],
                    ["tag"],
                    MemoryVersion.MemoryVersionStatus.Approved,
                    80,
                    "blob-body",
                    null,
                    DateTimeOffset.UtcNow.AddDays(-1),
                    null,
                    "test-model",
                    "prompt-1")
            ],
            null,
            null);
}
