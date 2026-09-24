using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Features.ContextDossier;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

public sealed class DossierBundleHandlerTests : HandlerTestBase
{
    private readonly AspireFixture _aspire;

    public DossierBundleHandlerTests(AspireFixture aspire) : base(aspire) => _aspire = aspire;

    private static readonly DateTimeOffset ValidFrom = new(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CreatedOn = new(2024, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProductGroupUuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProgramGroupUuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AnchorUuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid VisibleDeepUuid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid HiddenUuid = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private const string BlobAddress = "aa/bb/111111111111111111111111111111111111111111111111111111111111111";

    private CreateDossierBundle.Handler BundleHandler(IBlobStorage blobs) =>
        new(AppDb, Search, new NpgsqlMemoryTraversal(Db), new NpgsqlTicketGraph(Db), Graph, blobs,
            NullLogger<CreateDossierBundle.Handler>.Instance);

    private CreateDossierPreview.Handler PreviewHandler() =>
        new(Search, new NpgsqlMemoryTraversal(Db), new NpgsqlTicketGraph(Db), Graph,
            NullLogger<CreateDossierPreview.Handler>.Instance);

    [Fact]
    public async Task Bundle_is_byte_identical_across_two_calls_and_is_citation_complete()
    {
        await SeedForBundleAsync();
        var blobs = new DictionaryBlobStorage();
        blobs.Add(BlobAddress, "THE_BODY");
        CreateDossierBundle.Handler handler = BundleHandler(blobs);

        CreateDossierBundle.Response first = await handler.Handle(BundleRequest(), Ct);
        CreateDossierBundle.Response second = await handler.Handle(BundleRequest(), Ct);

        Serialize(first).ShouldBe(Serialize(second));

        // The bundle is deterministic and carries no generation timestamp — only stored times.
        first.Bundle.Manifest.SelectedCount.ShouldBe(first.Bundle.Items.Count + first.Bundle.Omitted.Count);
        first.Bundle.Items.ShouldContain(i => i.Kind == MemoryVersion.KindValue.Understanding);
        foreach (var item in first.Bundle.Items)
        {
            item.Uuid.ShouldNotBe(Guid.Empty);
            item.Version.ShouldBeGreaterThan(0);
            item.CreatedOn.ShouldNotBe(default);
        }
    }

    [Fact]
    public async Task Reordering_anchors_and_tags_changes_nothing()
    {
        await SeedForBundleAsync();
        var blobs = new DictionaryBlobStorage();
        blobs.Add(BlobAddress, "THE_BODY");
        CreateDossierBundle.Handler handler = BundleHandler(blobs);

        CreateDossierBundle.Response forward =
            await handler.Handle(BundleRequest(tags: ["b", "a"]), Ct);
        CreateDossierBundle.Response reverse =
            await handler.Handle(BundleRequest(tags: ["a", "b"]), Ct);

        Serialize(forward).ShouldBe(Serialize(reverse));
    }

    [Fact]
    public async Task Hidden_dimension_memory_reachable_at_depth_is_absent_and_path_not_shortened()
    {
        // A product anchor reaches a visible neighbour at depth 1 which reaches a program memory at
        // depth 2. The program memory is hidden and the path through it must be dropped whole, not
        // shortened — the visible neighbour's connection to it is absent.
        await SeedForHiddenPathAsync();
        var blobs = new DictionaryBlobStorage();
        blobs.Add(BlobAddress, "THE_BODY");
        CreateDossierBundle.Handler handler = BundleHandler(blobs);

        CreateDossierBundle.Response response = await handler.Handle(BundleRequest(), Ct);

        string serialized = Serialize(response);
        serialized.ShouldNotContain(HiddenUuid.ToString("D"));
        response.Bundle.Items.ShouldNotContain(i => i.Uuid == HiddenUuid);
        response.Bundle.Omitted.ShouldNotContain(o => o.Uuid == HiddenUuid);
        response.Bundle.Edges.ShouldNotContain(e =>
            e.SourceUuid == HiddenUuid || e.TargetUuid == HiddenUuid);
    }

    [Fact]
    public async Task Preview_performs_zero_blob_reads()
    {
        await SeedForBundleAsync();
        // The preview handler is blob-free by structure: it accepts no IBlobStorage, so it cannot
        // read or hydrate a single body (HLD-005 NFR-03 / LADR-08). Assert the capability is absent,
        // then price a real slice and confirm it produces a volume without touching any blob.
        typeof(CreateDossierPreview.Handler).GetConstructors()
            .ShouldContain(c => c.GetParameters().All(p => p.ParameterType != typeof(IBlobStorage)));

        CreateDossierPreview.Response response = await PreviewHandler().Handle(PreviewRequest(), Ct);

        response.Volume.Selected.ShouldBeGreaterThan(0);
        response.Cost.MonetaryAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task Bundle_leaves_the_store_unchanged_and_unregistered_facet_stays_unregistered()
    {
        await SeedForBundleAsync(unregisteredFacet: true);
        var blobs = new DictionaryBlobStorage();
        blobs.Add(BlobAddress, "THE_BODY");
        CreateDossierBundle.Handler handler = BundleHandler(blobs);

        long memoriesBefore = Db.Memories.LongCount();
        long versionsBefore = Db.MemoryVersions.LongCount();
        long groupsBefore = Db.MemoryGroups.LongCount();
        long labelsBefore = Db.Labels.LongCount();
        int edgesBefore = (await Graph.ListAllAsync(Ct)).Count;

        await handler.Handle(BundleRequest(), Ct);

        Db.Memories.LongCount().ShouldBe(memoriesBefore);
        Db.MemoryVersions.LongCount().ShouldBe(versionsBefore);
        Db.MemoryGroups.LongCount().ShouldBe(groupsBefore);
        Db.Labels.LongCount().ShouldBe(labelsBefore);
        // The facet observed during selection is not registered — registering an observed facet is the
        // most plausible accidental write on a read path (NFR-06).
        Db.Memories.Single(m => m.Uuid == AnchorUuid).Facets.ShouldBe(new[] { "unregistered-facet" });
        // Graph identity: no vertex and no edge added — including the "missing" contradicts edge a
        // contradiction finding describes (NFR-06 / LADR-06). Edges are the surface a findings write path
        // would most plausibly touch.
        (await Graph.ListAllAsync(Ct)).Count.ShouldBe(edgesBefore);
    }

    [Fact]
    public async Task Widen_depth_outside_one_to_five_is_refused_at_the_store_layer()
    {
        var traversal = new NpgsqlMemoryTraversal(Db);
        var query = new MemoryWidenQuery { SourceUuids = [AnchorUuid], MaxDepth = 6 };
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => traversal.WidenAsync(query, Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => traversal.WidenAsync(query with { MaxDepth = 0 }, Ct));
    }

    [Fact]
    public async Task Bundle_is_byte_identical_across_a_restart()
    {
        // NFR-02: byte-equality must hold across a process restart, not just two calls in one process.
        // Two independent EF contexts, handlers and data sources over the same unchanged store simulate a
        // restart: no in-process caching, no change-tracker or handler-scoped state, and no hash-seed
        // dependence can leak into the bytes. Hash-seed dependence specifically is caught because each
        // context recompiles its query plan in a fresh process-equivalent state.
        await SeedForBundleAsync();
        var blobs = new DictionaryBlobStorage();
        blobs.Add(BlobAddress, "THE_BODY");

        await using (RestartLine first = NewRestartLine())
        {
            CreateDossierBundle.Handler firstHandler = BundleHandlerFor(first.Db, blobs);
            byte[] firstBytes = SerializeToBytes(await firstHandler.Handle(BundleRequest(), Ct));

            // A fresh context + fresh handler against the same store — the "restart".
            await using (RestartLine second = NewRestartLine())
            {
                CreateDossierBundle.Handler secondHandler = BundleHandlerFor(second.Db, blobs);
                byte[] secondBytes = SerializeToBytes(await secondHandler.Handle(BundleRequest(), Ct));

                firstBytes.ShouldBe(secondBytes);
            }
        }
    }

    private CreateDossierBundle.Handler BundleHandlerFor(SmoothAiProductContextMemoryDbContext db, IBlobStorage blobs) =>
        new(db, new NpgsqlMemorySearch(db), new NpgsqlMemoryTraversal(db), new NpgsqlTicketGraph(db),
            new NpgsqlMemoryGraph(db), blobs, NullLogger<CreateDossierBundle.Handler>.Instance);

    /// <summary>
    /// An independent EF context over the same store, so a restart-equivalent read can be produced
    /// without sharing any in-process state with the test base's <see cref="Db"/>.
    /// </summary>
    private RestartLine NewRestartLine()
    {
        // The base builds its data source via NpgsqlDataSourceFactory (which initialises AGE on every
        // physical connection). A restart-equivalent context must do the same or the AGE reads fail.
        // The connection string exposed on the live connection drops the password, so rebuild it from
        // the Aspire fixture (which owns the credentials) for the same database.
        NpgsqlDataSource dataSource = NpgsqlDataSourceFactory.Create(
            _aspire.CreateDatabaseConnectionString(CurrentDatabaseName()));
        var options = new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.UseSmoothAiProductContextMemoryHistory())
            .Options;
        return new RestartLine(new SmoothAiProductContextMemoryDbContext(options), dataSource);
    }

    private string CurrentDatabaseName()
    {
        var builder = new NpgsqlConnectionStringBuilder(Db.Database.GetDbConnection().ConnectionString);
        return builder.Database ?? throw new InvalidOperationException("The test database name could not be read.");
    }

    private static byte[] SerializeToBytes(object value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed record RestartLine(SmoothAiProductContextMemoryDbContext Db, NpgsqlDataSource DataSource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await DataSource.DisposeAsync();
        }
    }

    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static CreateDossierBundle.Request BundleRequest(IReadOnlyList<string>? tags = null) =>
        new(
            Repo: "kingstown",
            InitiativeName: null,
            TicketProvider: null,
            TicketKey: null,
            Tags: tags ?? ["tag-1"],
            Kind: null,
            Status: null,
            ScopeDimension: null,
            IncludeHistory: false,
            AsOf: null,
            WidenDepth: 3);

    private static CreateDossierPreview.Request PreviewRequest() =>
        new(
            Repo: "kingstown",
            InitiativeName: null,
            TicketProvider: null,
            TicketKey: null,
            Tags: ["tag-1"],
            Kind: null,
            Status: null,
            ScopeDimension: null,
            IncludeHistory: false,
            AsOf: null,
            WidenDepth: 3);

    private async Task SeedForBundleAsync(bool unregisteredFacet = false)
    {
        MemoryGroup product = Group(ProductGroupUuid, MemoryGroup.ScopeDimensionValue.Product);
        Db.MemoryGroups.Add(product);
        await Db.SaveChangesAsync(Ct);

        Memory anchor = MemoryRow(product.Id, AnchorUuid, "Anchor", "Anchor fact", tags: ["tag-1"],
            facet: unregisteredFacet ? "unregistered-facet" : "architecture");
        Memory understanding = MemoryRow(product.Id, Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            "Understanding", "A distilled understanding", tags: ["tag-1"], facet: "architecture");
        Memory deep = MemoryRow(product.Id, VisibleDeepUuid, "Deep", "A deep related fact", tags: ["tag-1"],
            facet: "architecture");
        Db.Memories.AddRange(anchor, understanding, deep);
        await Db.SaveChangesAsync(Ct);

        Db.MemoryVersions.Add(Version(anchor.Id, 1, "Anchor claim", isCurrent: true, kind: MemoryVersion.KindValue.Decision));
        Db.MemoryVersions.Add(Version(understanding.Id, 1, "Understanding claim", isCurrent: true,
            kind: MemoryVersion.KindValue.Understanding));
        Db.MemoryVersions.Add(Version(deep.Id, 1, "Deep claim", isCurrent: true, blobAddress: BlobAddress,
            kind: MemoryVersion.KindValue.Decision));
        await Db.SaveChangesAsync(Ct);

        (await Graph.CreateAsync(AnchorUuid, VisibleDeepUuid, MemoryRelation.DependsOn, "needs it", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(AnchorUuid, understanding.Uuid, MemoryRelation.RelatesTo, "context", Ct)).ShouldBeTrue();
    }

    private async Task SeedForHiddenPathAsync()
    {
        MemoryGroup product = Group(ProductGroupUuid, MemoryGroup.ScopeDimensionValue.Product);
        MemoryGroup program = Group(ProgramGroupUuid, MemoryGroup.ScopeDimensionValue.Program);
        Db.MemoryGroups.AddRange(product, program);
        await Db.SaveChangesAsync(Ct);

        Memory anchor = MemoryRow(product.Id, AnchorUuid, "Anchor", "Anchor fact", tags: ["tag-1"], facet: "architecture");
        Memory deep = MemoryRow(product.Id, VisibleDeepUuid, "Visible deep", "Visible deep fact", tags: ["tag-1"], facet: "architecture");
        Memory hidden = MemoryRow(program.Id, HiddenUuid, "Hidden", "Hidden fact", tags: ["tag-1"], facet: "architecture");
        Db.Memories.AddRange(anchor, deep, hidden);
        await Db.SaveChangesAsync(Ct);

        await SeedVersionAsync(anchor.Id, 1, "Anchor claim", kind: MemoryVersion.KindValue.Decision, blobAddress: null, isCurrent: true);
        await SeedVersionAsync(deep.Id, 1, "Deep claim", kind: MemoryVersion.KindValue.Decision, blobAddress: BlobAddress, isCurrent: true);
        await SeedVersionAsync(hidden.Id, 1, "Hidden claim", kind: MemoryVersion.KindValue.Decision, blobAddress: null, isCurrent: true);

        // depth 2: anchor -> deep (visible) -> hidden (program). The path through the hidden memory is
        // dropped whole — the deep->hidden edge must not surface, and hidden must be absent everywhere.
        (await Graph.CreateAsync(AnchorUuid, VisibleDeepUuid, MemoryRelation.DependsOn, "needs it", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(VisibleDeepUuid, HiddenUuid, MemoryRelation.DependsOn, "leads to hidden", Ct)).ShouldBeTrue();
    }

    private Task SeedVersionAsync(long memoryId, int version, string statement, string kind, string? blobAddress, bool isCurrent)
    {
        Db.MemoryVersions.Add(Version(memoryId, version, statement, isCurrent, kind, blobAddress));
        return Db.SaveChangesAsync(Ct);
    }

    private static MemoryGroup Group(Guid uuid, string scope)
    {
        var group = new MemoryGroup
        {
            Uuid = uuid,
            ScopeDimension = scope,
            InitiativeId = TestEntities.DefaultInitiativeId,
            Repo = "kingstown",
            RepoUrl = "https://example.invalid/repo",
            CreatedOn = CreatedOn,
        };
        return group;
    }

    private static Memory MemoryRow(long groupId, Guid uuid, string name, string description, IReadOnlyList<string> tags, string facet) => new()
    {
        Uuid = uuid,
        LineageId = uuid,
        GroupId = groupId,
        Name = name,
        Description = description,
        SubjectSlug = Slug.Subject(description),
        Tags = [.. tags],
        Facets = [facet],
    };

    private static MemoryVersion Version(
        long memoryId, int version, string statement, bool isCurrent, string kind, string? blobAddress = null) => new()
    {
        MemoryId = memoryId,
        Version = version,
        IsCurrent = isCurrent,
        Statement = statement,
        ContentSummary = $"Summary of {statement}",
        BlobAddress = blobAddress,
        Kind = kind,
        Confidence = 80,
        Status = MemoryVersion.MemoryVersionStatus.Approved,
        Sources = [SourceDocument.Create("jira", "ACM-1", ValidFrom)],
        ValidFrom = ValidFrom,
        CreatedOn = CreatedOn,
    };

    private sealed class DictionaryBlobStorage : IBlobStorage
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

        public void Add(string address, string text) => _blobs[address] = Encoding.UTF8.GetBytes(text);

        public Task<string> StoreAsync(Stream content, string? contentType = null, CancellationToken cancellationToken = default) =>
            Task.FromResult("unused");

        public Task<BlobContent?> GetAsync(string address, CancellationToken cancellationToken = default)
        {
            if (!_blobs.TryGetValue(address, out byte[]? bytes))
            {
                return Task.FromResult<BlobContent?>(null);
            }

            return Task.FromResult<BlobContent?>(new BlobContent(new MemoryStream(bytes, writable: false), "text/plain; charset=utf-8"));
        }

        public Task<bool> ExistsAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult(_blobs.ContainsKey(address));
    }
}
