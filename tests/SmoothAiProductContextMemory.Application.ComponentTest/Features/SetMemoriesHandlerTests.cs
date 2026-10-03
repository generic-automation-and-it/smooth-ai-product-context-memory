using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Domain;
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
            Links = [new SetMemories.LinkWrite(a, b, MemoryRelation.DependsOn, "need")],
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
            Links = [new SetMemories.LinkWrite(a, b, MemoryRelation.DependsOn, "need")],
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

        var link = new SetMemories.LinkWrite(a, b, MemoryRelation.RelatesTo, "why");
        await handler.Handle(Write(group.Uuid, "Link a", "A2", a) with { Links = [link] }, Ct);

        SetMemories.Response second = await handler.Handle(
            Write(group.Uuid, "Link c", "C") with { Links = [link] },
            Ct);

        second.Created.ShouldBe(1);
        second.Skipped.ShouldBe(1);
        second.Linked.ShouldBe(0);
        (await Db.Memories.CountAsync(m => m.Description == "Link c", Ct)).ShouldBe(1);
    }

    /// <summary>
    /// A duplicate inside one batch is skipped and counted, not fatal — distinct from a
    /// standalone create, which refuses the same triple. The <c>Skipped</c> counter is
    /// shared with pre-existing/stale duplicates; the response does not distinguish the two.
    /// </summary>
    [Fact]
    public async Task Duplicate_link_in_same_batch_is_skipped_not_fatal()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler handler = NewHandler();
        Guid a = (await handler.Handle(Write(group.Uuid, "Batch a", "A"), Ct)).Items[0].Uuid!.Value;
        Guid b = (await handler.Handle(Write(group.Uuid, "Batch b", "B"), Ct)).Items[0].Uuid!.Value;

        var link = new SetMemories.LinkWrite(a, b, MemoryRelation.RelatesTo, "why");
        SetMemories.Response response = await handler.Handle(
            Write(group.Uuid, "Batch a", "A2", a) with { Links = [link, link] },
            Ct);

        response.Versioned.ShouldBe(1);
        response.Linked.ShouldBe(1);
        response.Skipped.ShouldBe(1);
        (await Graph.ListAllAsync(Ct)).Count(l => l.Relation == MemoryRelation.RelatesTo).ShouldBe(1);
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
    public async Task Ordered_versions_of_same_target_retain_loser_and_restore_winner()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler handler = NewHandler();
        Guid uuid = (await handler.Handle(Write(group.Uuid, "Subject", "Claim 1"), Ct)).Items[0].Uuid!.Value;

        SetMemories.Request batch = Write(group.Uuid, "Subject", "Losing claim", uuid) with
        {
            Items =
            [
                Write(group.Uuid, "Subject", "Losing claim", uuid).Items[0],
                Write(group.Uuid, "Subject", "Claim 1", uuid).Items[0],
            ],
        };

        SetMemories.Response dry = await handler.Handle(batch with { DryRun = true }, Ct);
        dry.Versioned.ShouldBe(2);

        SetMemories.Response written = await handler.Handle(batch, Ct);
        written.Versioned.ShouldBe(2);

        long memoryId = await Db.Memories.Where(m => m.Uuid == uuid).Select(m => m.Id).SingleAsync(Ct);
        MemoryVersion[] versions = await Db.MemoryVersions.AsNoTracking()
            .Where(v => v.MemoryId == memoryId)
            .OrderBy(v => v.Version)
            .ToArrayAsync(Ct);
        versions.Select(v => v.Statement).ShouldBe(["Claim 1", "Losing claim", "Claim 1"]);
        versions.Single(v => v.IsCurrent).Version.ShouldBe(3);
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

    [Fact]
    public async Task New_memory_links_use_caller_identities_and_match_dry_run()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        Guid existing = (await NewHandler().Handle(Write(group.Uuid, "Existing endpoint", "Existing"), Ct))
            .Items[0].Uuid!.Value;
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        SetMemories.Request request = Write(group.Uuid, "New endpoint A", "A") with
        {
            Items =
            [
                Write(group.Uuid, "New endpoint A", "A").Items[0] with { CreateUuid = first },
                Write(group.Uuid, "New endpoint B", "B").Items[0] with { CreateUuid = second },
            ],
            Links =
            [
                new(first, existing, MemoryRelation.DependsOn, "new to existing"),
                new(existing, second, MemoryRelation.RelatesTo, "existing to new"),
                new(first, second, MemoryRelation.Implements, "new to new"),
                new(first, second, MemoryRelation.Implements, "duplicate"),
            ],
        };

        SetMemories.Response dry = await NewHandler().Handle(request with { DryRun = true }, Ct);
        dry.Items.Select(i => i.Uuid).ShouldBe([first, second]);
        dry.Linked.ShouldBe(3);
        dry.Skipped.ShouldBe(1);

        SetMemories.Response written = await NewHandler().Handle(request, Ct);
        written.Items.Select(i => i.Uuid).ShouldBe([first, second]);
        written.Linked.ShouldBe(dry.Linked);
        written.Skipped.ShouldBe(dry.Skipped);

        IReadOnlyList<SmoothAiProductContextMemory.Application.Abstractions.MemoryRelationship> links =
            await Graph.ListAllAsync(Ct);
        links.ShouldContain(l => l.SourceUuid == first && l.TargetUuid == existing);
        links.ShouldContain(l => l.SourceUuid == existing && l.TargetUuid == second);
        links.ShouldContain(l => l.SourceUuid == first && l.TargetUuid == second);
    }

    [Fact]
    public async Task Graph_failure_rolls_back_memories_versions_and_edges()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        var failingGraph = new FailAfterFirstCreateGraph(Graph);
        var handler = new SetMemories.Handler(
            AppDb,
            failingGraph,
            Blob,
            ErrorMapper,
            Loggers.CreateLogger<SetMemories.Handler>(), new SmoothAiProductContextMemory.Infrastructure.Persistence.NpgsqlCorpusCommitStore(Db));
        SetMemories.Request request = Write(group.Uuid, "Rollback A", "A") with
        {
            Items =
            [
                Write(group.Uuid, "Rollback A", "A").Items[0] with { CreateUuid = first },
                Write(group.Uuid, "Rollback B", "B").Items[0] with { CreateUuid = second },
            ],
            Links =
            [
                new(first, second, MemoryRelation.DependsOn, "first edge"),
                new(second, first, MemoryRelation.RelatesTo, "forced failure"),
            ],
        };

        await Should.ThrowAsync<InvalidOperationException>(async () => await handler.Handle(request, Ct));
        Db.ChangeTracker.Clear();

        (await Db.Memories.AnyAsync(m => m.Uuid == first || m.Uuid == second, Ct)).ShouldBeFalse();
        (await Graph.ListAllAsync(Ct)).ShouldNotContain(l => l.SourceUuid == first || l.TargetUuid == first);
    }

    [Fact]
    public async Task Divergence_kind_is_counted_only_when_created()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        Guid existing = (await NewHandler().Handle(Write(group.Uuid, "Storage engine", "Use PostgreSQL"), Ct))
            .Items[0].Uuid!.Value;
        Guid candidate = Guid.NewGuid();
        Guid divergence = Guid.NewGuid();
        SetMemories.Request request = Write(group.Uuid, $"Unresolved alternative to {existing} ({candidate})", "Use SQLite") with
        {
            Items =
            [
                Write(group.Uuid, $"Unresolved alternative to {existing} ({candidate})", "Use SQLite").Items[0] with { CreateUuid = candidate },
                Write(group.Uuid, "Open conflict", "Sources disagree").Items[0] with
                {
                    CreateUuid = divergence,
                    Kind = MemoryVersion.KindValue.Divergence,
                    Status = MemoryVersion.MemoryVersionStatus.Proposed,
                },
            ],
            Links =
            [
                new(divergence, existing, MemoryRelation.Contradicts, "first side"),
                new(divergence, candidate, MemoryRelation.Contradicts, "second side"),
            ],
        };

        SetMemories.Response dry = await NewHandler().Handle(request with { DryRun = true }, Ct);
        dry.Diverged.ShouldBe(1);
        dry.Linked.ShouldBe(2);

        SetMemories.Response written = await NewHandler().Handle(request, Ct);
        written.Diverged.ShouldBe(1);
        written.Linked.ShouldBe(2);
        (await Graph.ListAllAsync(Ct)).Count(l => l.SourceUuid == divergence
            && l.Relation == MemoryRelation.Contradicts).ShouldBe(2);
    }

    [Fact]
    public async Task Cancelled_token_aborts_the_write_without_committing()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await NewHandler().Handle(Write(group.Uuid, "Cancelled subject", "Claim"), cts.Token));

        (await Db.Memories.AsNoTracking().CountAsync(m => m.GroupId == group.Id, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Duplicate_subject_in_batch_conflicts_before_any_write()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        SetMemories.Request request = new(
            group.Uuid,
            [
                Write(group.Uuid, "Shared subject", "Claim 1").Items[0],
                Write(group.Uuid, "Shared subject", "Claim 2").Items[0],
            ],
            null,
            null);

        await Should.ThrowAsync<ConflictException>(async () => await NewHandler().Handle(request, Ct));

        (await Db.Memories.AsNoTracking().CountAsync(m => m.GroupId == group.Id, Ct)).ShouldBe(0);
    }

    private sealed class FailAfterFirstCreateGraph(IMemoryGraph inner) : IMemoryGraph
    {
        private int _creates;

        public Task<bool> ExistsAsync(Guid sourceUuid, Guid targetUuid, string relation, CancellationToken cancellationToken) =>
            inner.ExistsAsync(sourceUuid, targetUuid, relation, cancellationToken);

        public async Task<bool> CreateAsync(
            Guid sourceUuid,
            Guid targetUuid,
            string relation,
            string reason,
            CancellationToken cancellationToken)
        {
            if (++_creates == 2)
            {
                throw new InvalidOperationException("Injected graph failure.");
            }

            return await inner.CreateAsync(sourceUuid, targetUuid, relation, reason, cancellationToken);
        }

        public Task<IReadOnlyList<MemoryRelationship>> ListAllAsync(CancellationToken cancellationToken) =>
            inner.ListAllAsync(cancellationToken);

        public Task<IReadOnlyList<MemoryRelationship>> ListTouchingAsync(Guid uuid, CancellationToken cancellationToken) =>
            inner.ListTouchingAsync(uuid, cancellationToken);

        public Task<IReadOnlyList<MemoryRelationship>> ListEdgesAsync(IReadOnlyCollection<Guid> uuids, CancellationToken cancellationToken) =>
            inner.ListEdgesAsync(uuids, cancellationToken);
    }

    private SetMemories.Handler NewHandler() =>
        new(AppDb, Graph, Blob, ErrorMapper, Loggers.CreateLogger<SetMemories.Handler>(), new SmoothAiProductContextMemory.Infrastructure.Persistence.NpgsqlCorpusCommitStore(Db));

    /// <summary>
    /// A version target must resolve inside the group the write names. The lookup was by uuid alone
    /// while the create path already scoped its slug check to the group, so a write naming group A
    /// could bump is_current on a memory belonging to group B — group isolation was not an invariant
    /// on the version path. The target is therefore reported not-found rather than silently versioned.
    /// </summary>
    [Fact]
    public async Task Version_target_in_another_group_is_not_found_and_writes_nothing()
    {
        MemoryGroup owner = TestEntities.NewGroup();
        MemoryGroup other = TestEntities.NewGroup();
        Db.MemoryGroups.AddRange(owner, other);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Response created = await NewHandler().Handle(
            Write(owner.Uuid, "Subject", "Claim 1"), Ct);
        Guid uuid = created.Items[0].Uuid.ShouldNotBeNull();

        await Should.ThrowAsync<NotFoundException>(() => NewHandler().Handle(
            Write(other.Uuid, "Subject", "Claim 2", uuid), Ct).AsTask());

        // The refused write must leave the owning group's memory exactly as it was: one version,
        // still current, and the original statement intact.
        long memoryId = await Db.Memories.Where(m => m.Uuid == uuid).Select(m => m.Id).SingleAsync(Ct);
        List<MemoryVersion> versions = await Db.MemoryVersions.AsNoTracking()
            .Where(v => v.MemoryId == memoryId)
            .ToListAsync(Ct);

        versions.Count.ShouldBe(1);
        versions.Count(v => v.IsCurrent).ShouldBe(1);
        versions.Single().Version.ShouldBe(1);
        versions.Single().Statement.ShouldBe("Claim 1");
    }

    /// <summary>
    /// Identity is group-scoped (HLD-002 LADR-01/04): the same subject captured under a second group
    /// is a separate memory, not a version of the first and not a duplicate-subject conflict.
    /// </summary>
    [Fact]
    public async Task Same_subject_in_two_groups_creates_two_distinct_memories()
    {
        MemoryGroup first = TestEntities.NewGroup();
        MemoryGroup second = TestEntities.NewGroup();
        Db.MemoryGroups.AddRange(first, second);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Response inFirst = await NewHandler().Handle(Write(first.Uuid, "Shared subject", "First claim"), Ct);
        SetMemories.Response dryInSecond = await NewHandler().Handle(
            Write(second.Uuid, "Shared subject", "Second claim") with { DryRun = true }, Ct);
        SetMemories.Response inSecond = await NewHandler().Handle(Write(second.Uuid, "Shared subject", "Second claim"), Ct);

        dryInSecond.Created.ShouldBe(1);
        dryInSecond.Versioned.ShouldBe(0);
        inFirst.Created.ShouldBe(1);
        inSecond.Created.ShouldBe(1);
        inSecond.Versioned.ShouldBe(0);
        Guid firstUuid = inFirst.Items[0].Uuid.ShouldNotBeNull();
        Guid secondUuid = inSecond.Items[0].Uuid.ShouldNotBeNull();
        secondUuid.ShouldNotBe(firstUuid);

        var memories = await Db.Memories.AsNoTracking()
            .Where(m => m.Uuid == firstUuid || m.Uuid == secondUuid)
            .Select(m => new
            {
                m.Uuid,
                m.GroupId,
                m.SubjectSlug,
                Statements = m.Versions.OrderBy(v => v.Version).Select(v => v.Statement).ToArray(),
            })
            .ToArrayAsync(Ct);

        memories.Length.ShouldBe(2);
        memories.Select(m => m.SubjectSlug).Distinct().Count().ShouldBe(1);
        memories.Single(m => m.Uuid == firstUuid).GroupId.ShouldBe(first.Id);
        memories.Single(m => m.Uuid == firstUuid).Statements.ShouldBe(["First claim"]);
        memories.Single(m => m.Uuid == secondUuid).GroupId.ShouldBe(second.Id);
        memories.Single(m => m.Uuid == secondUuid).Statements.ShouldBe(["Second claim"]);
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
