using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Application.Features.Groups;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Storage;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

public sealed class CorpusCommitBoundaryTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    private NpgsqlCorpusCommitStore Store => new(Db);
    private SetMemories.Handler Writer => new(AppDb, Graph, Blob, ErrorMapper, Loggers.CreateLogger<SetMemories.Handler>(), Store);

    [Fact]
    public async Task Retry_of_create_and_version_returns_original_result_without_another_version()
    {
        MemoryGroup group = await GroupAsync();
        CorpusState state = await Store.GetStateAsync(Ct);
        SetMemories.Request create = Write(group.Uuid, "subject", "first") with
        {
            OperationKey = "create", ExpectedCorpusEpoch = state.Epoch, ExpectedCorpusRevision = state.Revision,
        };
        SetMemories.Response first = await Writer.Handle(create, Ct);
        SetMemories.Response replay = await Writer.Handle(create, Ct);
        replay.ShouldBeEquivalentTo(first);
        state = await Store.GetStateAsync(Ct);
        SetMemories.Request bump = Write(group.Uuid, "subject", "second", first.Items[0].Uuid) with
        {
            OperationKey = "bump", ExpectedCorpusEpoch = state.Epoch, ExpectedCorpusRevision = state.Revision,
            Items = [Write(group.Uuid, "subject", "second", first.Items[0].Uuid).Items[0] with { ExpectedVersion = 1 }],
        };
        SetMemories.Response bumped = await Writer.Handle(bump, Ct);
        (await Writer.Handle(bump, Ct)).ShouldBeEquivalentTo(bumped);
        (await Db.MemoryVersions.CountAsync(Ct)).ShouldBe(2);
        (await Store.FindAsync("bump", Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Reusing_committed_key_with_different_payload_conflicts()
    {
        MemoryGroup group = await GroupAsync();
        CorpusState state = await Store.GetStateAsync(Ct);
        SetMemories.Request request = Write(group.Uuid, "subject", "first") with { OperationKey = "same", ExpectedCorpusEpoch = state.Epoch };
        await Writer.Handle(request, Ct);
        await Should.ThrowAsync<ConflictException>(async () => await Writer.Handle(request with
        {
            Items = [request.Items[0] with { Statement = "changed" }],
        }, Ct));
        (await Db.MemoryVersions.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Direct_writer_changes_revision_and_vetoes_stale_service_create()
    {
        MemoryGroup group = await GroupAsync();
        CorpusState before = await Store.GetStateAsync(Ct);
        await Writer.Handle(Write(group.Uuid, "direct subject", "direct fact"), Ct);
        CorpusState after = await Store.GetStateAsync(Ct);
        after.Revision.ShouldBeGreaterThan(before.Revision);
        await Should.ThrowAsync<ConflictException>(async () => await Writer.Handle(Write(group.Uuid, "service subject", "service fact") with
        {
            OperationKey = "stale", ExpectedCorpusEpoch = before.Epoch, ExpectedCorpusRevision = before.Revision,
        }, Ct));
        (await Store.FindAsync("stale", Ct)).ShouldBeNull();
        (await Db.Memories.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Stale_expected_version_conflicts_without_receipt()
    {
        MemoryGroup group = await GroupAsync();
        Guid uuid = (await Writer.Handle(Write(group.Uuid, "subject", "first"), Ct)).Items[0].Uuid!.Value;
        await Writer.Handle(Write(group.Uuid, "subject", "second", uuid), Ct);
        SetMemories.Request request = Write(group.Uuid, "subject", "third", uuid);
        await Should.ThrowAsync<ConflictException>(async () => await Writer.Handle(request with
        {
            Items = [request.Items[0] with { ExpectedVersion = 1 }],
        }, Ct));
        (await Db.MemoryVersions.CountAsync(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task No_ticket_group_creation_replays_the_original_group_and_local_ticket()
    {
        CorpusState state = await Store.GetStateAsync(Ct);
        var request = new ResolveGroup.Request(null, "synthetic/repo", null, null, null, null, "work", null,
            "group", state.Epoch, state.Revision);
        var handler = new ResolveGroup.Handler(AppDb, ErrorMapper, new NpgsqlTicketGraph(Db), Loggers.CreateLogger<ResolveGroup.Handler>(), Store);
        ResolveGroup.Response created = await handler.Handle(request, Ct);
        CorpusCommit.Hash(await handler.Handle(request, Ct)).ShouldBe(CorpusCommit.Hash(created));
        (await Db.MemoryGroups.CountAsync(Ct)).ShouldBe(1);
        (await new LookupGroups.Handler(AppDb).Handle(new LookupGroups.Request(Repo: "synthetic/repo"), Ct))
            .Items.ShouldHaveSingleItem().Uuid.ShouldBe(created.Uuid);
    }

    [Fact]
    public async Task Evidence_metadata_survives_ordinary_history_query_and_legacy_stays_unknown()
    {
        MemoryGroup group = await GroupAsync();
        SetMemories.Request request = Write(group.Uuid, "subject", "approved intent");
        var evidence = new EvidenceDocument { Category = "approved_intent", Authority = "practitioner", AuthorityReference = "message:1", Applicability = "proposal" };
        SetMemories.Response result = await Writer.Handle(request with
        {
            Items = [request.Items[0] with { Sources = [new SourceInput("message", "message:1", DateTimeOffset.UtcNow, evidence)] }],
        }, Ct);
        GetMemoryVersions.Response versions = await new GetMemoryVersions.Handler(AppDb, Loggers.CreateLogger<GetMemoryVersions.Handler>())
            .Handle(new GetMemoryVersions.Request(result.Items[0].Uuid!.Value), Ct);
        versions.Items.ShouldHaveSingleItem().Sources.ShouldHaveSingleItem().Evidence.ShouldNotBeNull().Category.ShouldBe("approved_intent");
        SourceDocument.Create("legacy", "reference").Evidence.ShouldBeNull();
    }

    [Fact]
    public async Task Snapshot_restores_receipts_and_rotates_epoch_even_for_the_same_corpus()
    {
        MemoryGroup group = await GroupAsync();
        CorpusState state = await Store.GetStateAsync(Ct);
        SetMemories.Request request = Write(group.Uuid, "subject", "first") with { OperationKey = "saved", ExpectedCorpusEpoch = state.Epoch };
        await Writer.Handle(request, Ct);
        var repository = new NpgsqlSnapshotRepository(Blob, (IBlobCatalog)Blob);
        var snapshot = await repository.CaptureAsync(ConnectionString, Ct);
        snapshot.Capture.OperationReceipts.ShouldNotBeNull().ShouldHaveSingleItem().OperationKey.ShouldBe("saved");
        var restored = await repository.RestoreAsync(ConnectionString, snapshot.Capture, snapshot.Counts, true, Ct);
        restored.Committed.ShouldBeTrue();
        (await Store.FindAsync("saved", Ct)).ShouldNotBeNull();
        CorpusState restoredState = await Store.GetStateAsync(Ct);
        restoredState.Epoch.ShouldNotBe(state.Epoch);
        await Should.ThrowAsync<ConflictException>(async () => await Writer.Handle(request, Ct));
        (await Db.MemoryVersions.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_direct_version_writers_replan_under_the_shared_transaction_lock()
    {
        MemoryGroup group = await GroupAsync();
        Guid uuid = (await Writer.Handle(Write(group.Uuid, "subject", "first"), Ct)).Items[0].Uuid!.Value;
        await using var first = NewContext();
        await using var second = NewContext();
        SetMemories.Handler firstWriter = WriterFor(first);
        SetMemories.Handler secondWriter = WriterFor(second);
        await Task.WhenAll(
            firstWriter.Handle(Write(group.Uuid, "subject", "second", uuid), Ct).AsTask(),
            secondWriter.Handle(Write(group.Uuid, "subject", "third", uuid), Ct).AsTask());
        (await Db.MemoryVersions.AsNoTracking().OrderBy(v => v.Version).Select(v => v.Version).ToArrayAsync(Ct)).ShouldBe([1, 2, 3]);
        (await Db.MemoryVersions.CountAsync(v => v.IsCurrent, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_same_key_creates_return_one_committed_result()
    {
        MemoryGroup group = await GroupAsync();
        CorpusState state = await Store.GetStateAsync(Ct);
        SetMemories.Request request = Write(group.Uuid, "subject", "first") with
        {
            OperationKey = "concurrent", ExpectedCorpusEpoch = state.Epoch, ExpectedCorpusRevision = state.Revision,
        };
        await using var first = NewContext();
        await using var second = NewContext();
        SetMemories.Response[] results = await Task.WhenAll(
            WriterFor(first).Handle(request, Ct).AsTask(), WriterFor(second).Handle(request, Ct).AsTask());
        CorpusCommit.Hash(results[0]).ShouldBe(CorpusCommit.Hash(results[1]));
        (await Db.MemoryVersions.CountAsync(Ct)).ShouldBe(1);
        (await Store.FindAsync("concurrent", Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Equivalent_claim_with_added_evidence_is_one_append_only_version_and_replays_once()
    {
        MemoryGroup group = await GroupAsync();
        SetMemories.Response first = await Writer.Handle(Write(group.Uuid, "subject", "same fact"), Ct);
        CorpusState state = await Store.GetStateAsync(Ct);
        SetMemories.Request request = Write(group.Uuid, "subject", "same fact", first.Items[0].Uuid) with
        {
            OperationKey = "add-source", ExpectedCorpusEpoch = state.Epoch, ExpectedCorpusRevision = state.Revision,
        };
        request = request with { Items = [request.Items[0] with
        {
            ExpectedVersion = 1,
            Sources = [new SourceInput("message", "message:added", null, new EvidenceDocument { Category = "observed_implementation" })],
        }] };
        await Writer.Handle(request, Ct);
        await Writer.Handle(request, Ct);
        MemoryVersion[] versions = await Db.MemoryVersions.AsNoTracking().OrderBy(v => v.Version).ToArrayAsync(Ct);
        versions.Length.ShouldBe(2);
        versions[0].Sources.ShouldBeEmpty();
        versions[1].Sources.ShouldHaveSingleItem().Evidence.ShouldNotBeNull().Category.ShouldBe("observed_implementation");
        versions.Select(v => v.Statement).Distinct().ShouldHaveSingleItem().ShouldBe("same fact");
    }

    [Fact]
    public async Task Direct_graph_and_group_writes_advance_the_corpus_revision()
    {
        MemoryGroup group = await GroupAsync();
        Guid first = (await Writer.Handle(Write(group.Uuid, "first", "first"), Ct)).Items[0].Uuid!.Value;
        Guid second = (await Writer.Handle(Write(group.Uuid, "second", "second"), Ct)).Items[0].Uuid!.Value;
        CorpusState before = await Store.GetStateAsync(Ct);
        await Graph.CreateAsync(first, second, "depends_on", "synthetic reason", Ct);
        CorpusState afterGraph = await Store.GetStateAsync(Ct);
        afterGraph.Revision.ShouldBeGreaterThan(before.Revision);
        Db.DiscardTrackedState();
        MemoryGroup stored = await Db.MemoryGroups.SingleAsync(Ct);
        stored.Repo = "synthetic/changed";
        await Db.SaveChangesAsync(Ct);
        (await Store.GetStateAsync(Ct)).Revision.ShouldBeGreaterThan(afterGraph.Revision);
    }

    [Fact]
    public async Task Direct_ticket_parent_write_advances_revision()
    {
        Db.MemoryGroups.AddRange(
            TestEntities.NewGroup(tickets: [TicketDocument.Create("local", "child", "")]),
            TestEntities.NewGroup(tickets: [TicketDocument.Create("local", "parent", "")]));
        await Db.SaveChangesAsync(Ct);
        CorpusState before = await Store.GetStateAsync(Ct);
        await new NpgsqlTicketGraph(Db).ChangeParentAsync(new TicketParentChange(
            new TicketIdentity("local", "child"), new TicketIdentity("local", "parent"), null,
            "synthetic hierarchy", "message:1", null), Ct);
        (await Store.GetStateAsync(Ct)).Revision.ShouldBeGreaterThan(before.Revision);
    }

    [Fact]
    public async Task Existing_evidence_cannot_be_mutated_in_append_only_history()
    {
        MemoryGroup group = await GroupAsync();
        SetMemories.Request request = Write(group.Uuid, "subject", "first");
        await Writer.Handle(request with { Items = [request.Items[0] with
        {
            Sources = [new SourceInput("message", "message:1", null, new EvidenceDocument { Category = "approved_intent" })],
        }] }, Ct);
        Db.DiscardTrackedState();
        MemoryVersion version = await Db.MemoryVersions.SingleAsync(Ct);
        version.Sources = [SourceDocument.Create("message", "message:2", evidence: new EvidenceDocument { Category = "observed_implementation" })];
        await Should.ThrowAsync<DbUpdateException>(async () => await Db.SaveChangesAsync(Ct));
        Db.DiscardTrackedState();
        (await Db.MemoryVersions.AsNoTracking().SingleAsync(Ct)).Sources.ShouldHaveSingleItem()
            .Evidence.ShouldNotBeNull().Category.ShouldBe("approved_intent");
    }

    private SmoothAiProductContextMemoryDbContext NewContext() => new(
        new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
            .UseNpgsql(TestDataSource).Options);

    private SetMemories.Handler WriterFor(SmoothAiProductContextMemoryDbContext context) => new(
        context, new NpgsqlMemoryGraph(context), Blob, ErrorMapper, Loggers.CreateLogger<SetMemories.Handler>(), new NpgsqlCorpusCommitStore(context));

    private async Task<MemoryGroup> GroupAsync()
    {
        MemoryGroup group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        return group;
    }

    private static SetMemories.Request Write(Guid group, string subject, string statement, Guid? uuid = null) => new(group,
        [new SetMemories.MemoryWrite(uuid, subject, subject, statement, statement, "rule", null, null, "proposed", 80,
            null, null, DateTimeOffset.Parse("2026-10-03T00:00:00Z"), null, null, null)], null, null);
}
