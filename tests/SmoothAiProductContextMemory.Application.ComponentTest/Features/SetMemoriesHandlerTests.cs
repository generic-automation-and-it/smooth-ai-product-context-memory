using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
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

        SetMemories.Handler handler = NewHandler();
        SetMemories.Response first = await handler.Handle(Write(group.Uuid, "Subject", "Claim 1"), Ct);
        Guid uuid = first.Items[0].Uuid.ShouldNotBeNull();

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

        SetMemories.Response dry = await NewHandler().Handle(
            Write(group.Uuid, "Dry subject", "Dry claim") with { DryRun = true },
            Ct);

        dry.Created.ShouldBe(1);
        dry.Items[0].BlobAddress.ShouldBeNull();
        dry.Items[0].Uuid.ShouldBeNull();
        (await Db.Memories.CountAsync(Ct)).ShouldBe(0);
    }

    /// <summary>
    /// The dry run is the only pre-write veto point, so its verdict has to be the verdict the write
    /// would reach — including the parts a shortcut path would miss: an already-linked pair and an
    /// already-registered label.
    /// </summary>
    [Fact]
    public async Task Dry_run_predicts_skipped_links_and_labels_like_the_write()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        Db.Labels.Add(new Label { Name = "already-known", Status = Label.LabelStatus.Active });
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler handler = NewHandler();
        Guid a = (await handler.Handle(Write(group.Uuid, "A subject", "A"), Ct)).Items[0].Uuid!.Value;
        Guid b = (await handler.Handle(Write(group.Uuid, "B subject", "B"), Ct)).Items[0].Uuid!.Value;

        SetMemories.Request bump = Write(group.Uuid, "A subject", "A2", a) with
        {
            Links = [new SetMemories.LinkWrite(a, b, MemoryLink.RelationValue.DependsOn, "need")],
            LabelsProposed = ["already-known", "brand-new"],
        };

        SetMemories.Response predicted = await handler.Handle(bump with { DryRun = true }, Ct);
        predicted.Linked.ShouldBe(1);
        predicted.Skipped.ShouldBe(0);
        predicted.LabelsProposed.ShouldBe(1);

        SetMemories.Response written = await handler.Handle(bump, Ct);
        written.Linked.ShouldBe(predicted.Linked);
        written.Skipped.ShouldBe(predicted.Skipped);
        written.LabelsProposed.ShouldBe(predicted.LabelsProposed);

        // Same request again: the link now exists, so both paths must agree it is skipped.
        SetMemories.Request repeat = Write(group.Uuid, "A subject", "A3", a) with
        {
            Links = [new SetMemories.LinkWrite(a, b, MemoryLink.RelationValue.DependsOn, "need")],
            LabelsProposed = ["already-known", "brand-new"],
        };

        SetMemories.Response predictedRepeat = await handler.Handle(repeat with { DryRun = true }, Ct);
        predictedRepeat.Linked.ShouldBe(0);
        predictedRepeat.Skipped.ShouldBe(1);
        predictedRepeat.LabelsProposed.ShouldBe(0);

        SetMemories.Response writtenRepeat = await handler.Handle(repeat, Ct);
        writtenRepeat.Linked.ShouldBe(predictedRepeat.Linked);
        writtenRepeat.Skipped.ShouldBe(predictedRepeat.Skipped);
        writtenRepeat.LabelsProposed.ShouldBe(predictedRepeat.LabelsProposed);
    }

    /// <summary>A duplicate link must not discard the memories it was derived from.</summary>
    [Fact]
    public async Task Existing_link_is_skipped_not_fatal()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler handler = NewHandler();
        Guid a = (await handler.Handle(Write(group.Uuid, "Link a", "A"), Ct)).Items[0].Uuid!.Value;
        Guid b = (await handler.Handle(Write(group.Uuid, "Link b", "B"), Ct)).Items[0].Uuid!.Value;

        var link = new SetMemories.LinkWrite(a, b, MemoryLink.RelationValue.RelatesTo, "why");
        await handler.Handle(Write(group.Uuid, "Link a", "A2", a) with { Links = [link] }, Ct);

        SetMemories.Response second = await handler.Handle(
            Write(group.Uuid, "Link c", "C") with { Links = [link] },
            Ct);

        second.Created.ShouldBe(1);
        second.Skipped.ShouldBe(1);
        second.Linked.ShouldBe(0);
        (await Db.Memories.CountAsync(m => m.Description == "Link c", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Duplicate_subject_in_group_is_a_conflict_on_both_paths()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler handler = NewHandler();
        await handler.Handle(Write(group.Uuid, "Taken subject", "Claim"), Ct);

        await Should.ThrowAsync<ConflictException>(async () =>
            await handler.Handle(Write(group.Uuid, "Taken subject", "Other claim") with { DryRun = true }, Ct));
        await Should.ThrowAsync<ConflictException>(async () =>
            await handler.Handle(Write(group.Uuid, "Taken subject", "Other claim"), Ct));
    }

    [Fact]
    public async Task Jsonb_v_round_trips_on_sources()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        SetMemories.MemoryWrite item = Write(group.Uuid, "Sourced", "Claim").Items[0] with
        {
            Sources = [new("jira", "ACM-1", DateTimeOffset.UtcNow)]
        };
        await NewHandler().Handle(Write(group.Uuid, "Sourced", "Claim") with { Items = [item] }, Ct);

        MemoryVersion version = await Db.MemoryVersions.AsNoTracking().SingleAsync(Ct);
        version.Sources.Count.ShouldBe(1);
        version.Sources[0].V.ShouldBe(JsonShapeDocument.CurrentShapeVersion);
        version.Sources[0].Kind.ShouldBe("jira");
    }

    private SetMemories.Handler NewHandler() =>
        new(AppDb, Blob, ErrorMapper, Loggers.CreateLogger<SetMemories.Handler>());

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
