using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.ContextDossier;
using SmoothAiProductContextMemory.Application.Features.Export;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Export;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

/// <summary>
/// Store-level evidence for HLD-007 (understanding transfer). The load skill itself is file-only and
/// never reaches the store, so NFR-01's store guarantee is proven over the read paths that produce the
/// material a load consumes: query (both breadths), the forensic export and the dossier. Assertions use
/// identifiers and counts only — never memory bodies (NFR-05).
/// </summary>
public sealed class UnderstandingTransferStoreTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    private const string Repo = "repo-a";
    private const string SharedTag = "understanding-transfer";
    private const string UnregisteredFacet = "never-registered-facet";
    private const string UnregisteredKind = "never-registered-kind";

    [Fact]
    public async Task Default_load_read_paths_write_nothing_to_any_store()
    {
        Seeded seeded = await SeedAsync();
        var blobs = new CountingBlobStorage(Blob);
        StoreSnapshot before = await SnapshotAsync();
        int saveChangesBefore = SaveChanges.Count;
        before.Versions.ShouldBe(4);
        before.Vertices.ShouldBeGreaterThanOrEqualTo(2);
        before.Edges.ShouldBeGreaterThanOrEqualTo(1);
        (await Db.MemoryVersions.CountAsync(v => v.BlobAddress != null, Ct)).ShouldBeGreaterThan(0);

        QueryMemories.Handler query = NewQuery();
        (await query.Handle(Query() with { Kind = MemoryVersion.KindValue.Understanding }, Ct))
            .Items.Select(i => i.Uuid).ShouldBe([seeded.Understanding]);
        (await query.Handle(Query(), Ct)).Items.Count.ShouldBe(2);
        (await query.Handle(Query() with { Facets = [UnregisteredFacet], Kind = UnregisteredKind }, Ct))
            .Items.ShouldBeEmpty();

        string exportDir = Path.Combine(Path.GetTempPath(), $"understanding-load-{Guid.NewGuid():N}");
        try
        {
            ExportStore.Response export = await NewExport(blobs).Handle(new ExportStore.Request(exportDir), Ct);
            export.Memories.ShouldBe(2);
        }
        finally
        {
            if (Directory.Exists(exportDir))
            {
                Directory.Delete(exportDir, recursive: true);
            }
        }

        await NewBundle(blobs).Handle(BundleRequest(), Ct);
        await NewPreview().Handle(PreviewRequest(), Ct);

        (await SnapshotAsync()).ShouldBe(before);
        SaveChanges.Count.ShouldBe(saveChangesBefore);
        blobs.Stores.ShouldBe(0);
        (await Db.Labels.AnyAsync(l => l.Name == UnregisteredFacet || l.Name == UnregisteredKind, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Understanding_is_written_and_version_bumped_by_the_capture_path()
    {
        MemoryGroup group = await AddGroupAsync(repo: null);
        SetMemories.Handler set = NewSet();

        Guid uuid = (await set.Handle(Write(group.Uuid, "Understanding subject", "Answer 1"), Ct))
            .Items[0].Uuid.ShouldNotBeNull();
        SetMemories.Response restated = await set.Handle(
            Write(group.Uuid, "Understanding subject", "Answer 2", uuid), Ct);

        restated.Versioned.ShouldBe(1);
        restated.Created.ShouldBe(0);
        (await Db.Memories.CountAsync(Ct)).ShouldBe(1);

        MemoryVersion[] versions = await Db.MemoryVersions.AsNoTracking()
            .Where(v => v.Memory!.Uuid == uuid)
            .OrderBy(v => v.Version)
            .ToArrayAsync(Ct);
        versions.Select(v => v.Version).ShouldBe([1, 2]);
        versions.Single(v => v.IsCurrent).Version.ShouldBe(2);
        versions.ShouldAllBe(v => v.Kind == MemoryVersion.KindValue.Understanding);

        // A write that skipped preflight cannot duplicate the subject: the store refuses it.
        await Should.ThrowAsync<ConflictException>(async () =>
            await set.Handle(Write(group.Uuid, "Understanding subject", "Answer 3"), Ct));
        (await Db.Memories.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Cross_repo_reach_comes_from_default_scope_and_no_repo_anchor()
    {
        Seeded seeded = await SeedAsync();

        MemoryGroup understandingGroup = await Db.MemoryGroups.AsNoTracking()
            .SingleAsync(g => g.Uuid == seeded.UnderstandingGroup, Ct);
        understandingGroup.Repo.ShouldBeNull();
        understandingGroup.ScopeDimension.ShouldBe(MemoryGroup.ScopeDimensionValue.Product);

        QueryMemories.Handler query = NewQuery();

        (await query.Handle(Query(), Ct)).Items.Select(i => i.Uuid)
            .ShouldBe([seeded.Understanding, seeded.RepoDecision], ignoreOrder: true);
        (await query.Handle(Query() with { Kind = MemoryVersion.KindValue.Understanding }, Ct))
            .Items.Select(i => i.Uuid).ShouldBe([seeded.Understanding]);

        // The boundary BR-41 requires stating: a one-repository query does not reach an unanchored group.
        (await query.Handle(Query() with { Repo = Repo }, Ct))
            .Items.Select(i => i.Uuid).ShouldBe([seeded.RepoDecision]);
    }

    [Fact]
    public async Task Query_keeps_understanding_attribution()
    {
        Seeded seeded = await SeedAsync();

        MemoryVersion current = await Db.MemoryVersions.AsNoTracking()
            .SingleAsync(v => v.Memory!.Uuid == seeded.Understanding && v.IsCurrent, Ct);

        CheapMemory loaded = (await NewQuery().Handle(
                Query() with { Kind = MemoryVersion.KindValue.Understanding }, Ct))
            .Items.Single();

        loaded.Uuid.ShouldBe(seeded.Understanding);
        loaded.Version.ShouldBe(current.Version);
        loaded.Version.ShouldBe(2);
        loaded.CreatedOn.ShouldBe(current.CreatedOn, TimeSpan.FromMilliseconds(1));
        loaded.Kind.ShouldBe(MemoryVersion.KindValue.Understanding);
    }

    private async Task<Seeded> SeedAsync()
    {
        MemoryGroup understandingGroup = await AddGroupAsync(repo: null);
        MemoryGroup repoGroup = await AddGroupAsync(repo: Repo);
        Db.Labels.Add(new Label { Name = SharedTag, Status = Label.LabelStatus.Active });
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler set = NewSet();
        Guid understanding = (await set.Handle(Write(understandingGroup.Uuid, "Transferable lesson", "Answer 1"), Ct))
            .Items[0].Uuid!.Value;
        await set.Handle(Write(understandingGroup.Uuid, "Transferable lesson", "Answer 2", understanding), Ct);

        SetMemories.Request decision = Write(repoGroup.Uuid, "Repo decision", "Decided", kind: MemoryVersion.KindValue.Decision);
        Guid repoDecision = (await set.Handle(decision, Ct)).Items[0].Uuid!.Value;

        await set.Handle(
            Write(repoGroup.Uuid, "Repo decision", "Decided again", repoDecision, MemoryVersion.KindValue.Decision) with
            {
                Links = [new SetMemories.LinkWrite(repoDecision, understanding, MemoryRelation.RelatesTo, "applies")],
            },
            Ct);

        return new Seeded(understandingGroup.Uuid, understanding, repoDecision);
    }

    private async Task<MemoryGroup> AddGroupAsync(string? repo)
    {
        MemoryGroup group = TestEntities.NewGroup(repo: repo);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        return group;
    }

    private async Task<StoreSnapshot> SnapshotAsync()
    {
        Db.ChangeTracker.Clear();
        string[] current = await Db.MemoryVersions.AsNoTracking()
            .Where(v => v.IsCurrent)
            .OrderBy(v => v.MemoryId)
            .Select(v => v.MemoryId + ":" + v.Version)
            .ToArrayAsync(Ct);

        return new StoreSnapshot(
            await Db.Initiatives.CountAsync(Ct),
            await Db.Labels.CountAsync(Ct),
            await Db.MemoryGroups.CountAsync(Ct),
            await Db.GroupDescriptions.CountAsync(Ct),
            await Db.Memories.CountAsync(Ct),
            await Db.MemoryVersions.CountAsync(Ct),
            string.Join(",", current),
            await CountGraphAsync("MATCH (v) RETURN v"),
            await CountGraphAsync("MATCH ()-[e]->() RETURN e"));
    }

    private async Task<long> CountGraphAsync(string cypher)
    {
        DbConnection connection = Db.Database.GetDbConnection();
        await Db.Database.OpenConnectionAsync(Ct);
        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText =
                $"SELECT count(*) FROM ag_catalog.cypher('{AgeSession.GraphName}', $$ {cypher} $$) AS (x ag_catalog.agtype);";
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
        }
        finally
        {
            await Db.Database.CloseConnectionAsync();
        }
    }

    private SetMemories.Handler NewSet() =>
        new(AppDb, Graph, Blob, ErrorMapper, Loggers.CreateLogger<SetMemories.Handler>());

    private QueryMemories.Handler NewQuery() =>
        new(AppDb, Search, new NoopRecallFeedback(), Loggers.CreateLogger<QueryMemories.Handler>());

    private ExportStore.Handler NewExport(IBlobStorage blobs) =>
        new(AppDb, Graph, blobs, new FileSystemMarkdownExportSink(NullLogger<FileSystemMarkdownExportSink>.Instance),
            NullLogger<ExportStore.Handler>.Instance);

    private CreateDossierBundle.Handler NewBundle(IBlobStorage blobs) =>
        new(AppDb, Search, new NpgsqlMemoryTraversal(Db), new NpgsqlTicketGraph(Db), Graph, blobs,
            NullLogger<CreateDossierBundle.Handler>.Instance);

    private CreateDossierPreview.Handler NewPreview() =>
        new(Search, new NpgsqlMemoryTraversal(Db), new NpgsqlTicketGraph(Db), Graph,
            NullLogger<CreateDossierPreview.Handler>.Instance);

    private static QueryMemories.Request Query() =>
        new(null, null, null, null, null, null, null, null, null, null, null);

    private static CreateDossierBundle.Request BundleRequest() =>
        new(
            Repo: null,
            InitiativeName: null,
            TicketProvider: null,
            TicketKey: null,
            Tags: [SharedTag],
            Kind: null,
            Status: null,
            ScopeDimension: null,
            IncludeHistory: true,
            AsOf: null,
            WidenDepth: 3);

    private static CreateDossierPreview.Request PreviewRequest() =>
        new(
            Repo: null,
            InitiativeName: null,
            TicketProvider: null,
            TicketKey: null,
            Tags: [SharedTag],
            Kind: null,
            Status: null,
            ScopeDimension: null,
            IncludeHistory: true,
            AsOf: null,
            WidenDepth: 3);

    private static SetMemories.Request Write(
        Guid groupUuid,
        string description,
        string statement,
        Guid? uuid = null,
        string kind = MemoryVersion.KindValue.Understanding) =>
        new(
            groupUuid,
            [
                new SetMemories.MemoryWrite(
                    Uuid: uuid,
                    Name: "Name",
                    Description: description,
                    Statement: statement,
                    ContentSummary: "Why",
                    Kind: kind,
                    Facets: ["transfer"],
                    Tags: [SharedTag],
                    Status: MemoryVersion.MemoryVersionStatus.Approved,
                    Confidence: 80,
                    Content: "blob-body",
                    Sources: null,
                    ValidFrom: DateTimeOffset.UtcNow.AddDays(-1),
                    ValidUntil: null,
                    SummaryModel: "test-model",
                    SummaryPromptVersion: "prompt-1")
            ],
            null,
            null);

    private sealed record Seeded(Guid UnderstandingGroup, Guid Understanding, Guid RepoDecision);

    private sealed record StoreSnapshot(
        int Initiatives,
        int Labels,
        int Groups,
        int GroupDescriptions,
        int Memories,
        int Versions,
        string CurrentVersions,
        long Vertices,
        long Edges);

    /// <summary>
    /// Query records recall telemetry to <c>recall_feedback</c> (HLD-004), a SQL-only table outside the
    /// six entities; it is not store content, so it is out of NFR-01's scope and stubbed here.
    /// </summary>
    private sealed class NoopRecallFeedback : IRecallFeedback
    {
        public void Record(RecallFeedbackRecord[] records)
        {
        }
    }

    /// <summary>Delegates reads to the real store and counts any attempt to create an object.</summary>
    private sealed class CountingBlobStorage(IBlobStorage inner) : IBlobStorage
    {
        private int _stores;

        public int Stores => _stores;

        public Task<string> StoreAsync(Stream content, string? contentType = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stores);
            return inner.StoreAsync(content, contentType, cancellationToken);
        }

        public Task<BlobContent?> GetAsync(string address, CancellationToken cancellationToken = default) =>
            inner.GetAsync(address, cancellationToken);

        public Task<bool> ExistsAsync(string address, CancellationToken cancellationToken = default) =>
            inner.ExistsAsync(address, cancellationToken);
    }
}
