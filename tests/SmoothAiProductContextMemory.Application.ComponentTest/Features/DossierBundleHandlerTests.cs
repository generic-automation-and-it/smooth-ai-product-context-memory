using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;
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
    private static readonly Guid ProposedUuid = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd02");
    private static readonly Guid FutureAnchorUuid = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffff01");
    private static readonly Guid FutureWidenedUuid = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffff02");
    private static readonly Guid OrderingFirst = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeee01");
    private static readonly Guid OrderingLaterCapture = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeee02");
    private static readonly Guid OrderingTieLow = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeee03");
    private static readonly Guid OrderingTieHigh = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeee04");
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

        // Dropped whole, never shortened: the visible neighbour and its edge from the anchor survive.
        response.Bundle.Items.ShouldContain(i => i.Uuid == VisibleDeepUuid);
        response.Bundle.Edges.ShouldContain(e => e.SourceUuid == AnchorUuid && e.TargetUuid == VisibleDeepUuid);
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
    public async Task Ticket_anchor_truncated_empty_matches_preview_and_discloses_limits()
    {
        // A supplied ticket that hits a path/depth limit and yields zero eligible identities must be a
        // truncated-empty result, not a complete-empty one. The selection path propagates the ticket
        // traversal's disclosure flags (DossierSelection), and the bundle and preview over the same
        // selection must report the same limits (LADR-201). This is the only path that reaches the
        // zero-match disclosure; no prior test supplied a ticket anchor.
        await SeedForBundleAsync();
        var blobs = new DictionaryBlobStorage();
        blobs.Add(BlobAddress, "THE_BODY");

        var ticketGraph = new FakeTicketGraph(new TicketTraversalResult(
            Paths: [],
            Items: [],
            Disclosure: new TicketTraversalDisclosure(
                MaxDepth: 3, PathLimit: 50, MemoryLimit: 50,
                DepthLimitReached: true, PathLimitReached: true, MemoryLimitReached: false)));

        CreateDossierBundle.Handler bundle = new(
            AppDb, Search, new NpgsqlMemoryTraversal(Db), ticketGraph, Graph, blobs,
            NullLogger<CreateDossierBundle.Handler>.Instance);
        CreateDossierPreview.Handler preview = new(
            Search, new NpgsqlMemoryTraversal(Db), ticketGraph, Graph,
            NullLogger<CreateDossierPreview.Handler>.Instance);

        CreateDossierBundle.Request bundleRequest = BundleRequest(
            ticketProvider: "jira", ticketKey: "ACM-999");
        CreateDossierPreview.Request previewRequest = PreviewRequest(
            ticketProvider: "jira", ticketKey: "ACM-999");

        CreateDossierBundle.Response bundleResponse = await bundle.Handle(bundleRequest, Ct);
        CreateDossierPreview.Response previewResponse = await preview.Handle(previewRequest, Ct);

        bundleResponse.Bundle.Manifest.NoMatch.ShouldBeTrue();
        bundleResponse.Bundle.Manifest.LimitsHit.ShouldContain(l => l.Limit == DossierOmissionReason.DepthReached);
        bundleResponse.Bundle.Manifest.LimitsHit.ShouldContain(l => l.Limit == DossierOmissionReason.CapReached);
        bundleResponse.Bundle.Items.ShouldBeEmpty();

        // The preview over the same selection discloses the same limits — bundle never reports a cap
        // the consent artefact did not show.
        previewResponse.NoMatch.ShouldBeTrue();
        previewResponse.LimitsHit.ShouldBe(bundleResponse.Bundle.Manifest.LimitsHit);
    }

    [Fact]
    public async Task Ticket_anchor_with_a_status_reaches_the_memory_the_manifest_says_it_reached()
    {
        // The ticket traversal's identity set is the filter the anchor search then applies its own
        // status to, so a status that reached only the anchor search filtered a set the traversal had
        // already emptied. With the status absent from the traversal query the SQL took its
        // `@status IS NULL` branch, ExcludeProposed stayed at its true default, every proposed memory
        // was dropped before the anchor search ran, and `ticket + status=proposed` reported noMatch
        // every time — while RetrievalPolicy recorded "current-only, status=proposed", so the artefact a
        // caller repeats the selection from asserted the opposite of what the selection did.
        //
        // The fake below reproduces the traversal's real predicate rather than returning a fixed
        // result. That is what makes this a guard on the wiring: a fake that ignored the query would
        // hand back the proposed memory regardless and this test would pass with the defect present.
        await SeedForProposedBundleAsync();
        var blobs = new DictionaryBlobStorage();
        blobs.Add(BlobAddress, "THE_BODY");

        var ticketGraph = new FilteringTicketGraph([
            new(ProposedUuid, ProductGroupUuid, "Proposed", MemoryVersion.MemoryVersionStatus.Proposed),
            new(AnchorUuid, ProductGroupUuid, "Anchor", MemoryVersion.MemoryVersionStatus.Approved),
        ]);

        CreateDossierBundle.Handler bundle = new(
            AppDb, Search, new NpgsqlMemoryTraversal(Db), ticketGraph, Graph, blobs,
            NullLogger<CreateDossierBundle.Handler>.Instance);

        CreateDossierBundle.Response response = await bundle.Handle(
            BundleRequest(ticketProvider: "jira", ticketKey: "ACM-999", status: "proposed"), Ct);

        // The anchor's status reaches the traversal, and ExcludeProposed is left at its default: the
        // `@status IS NOT NULL` branch supersedes it, which is the same rule FindTicketPaths relies on.
        ticketGraph.LastQuery.ShouldNotBeNull();
        ticketGraph.LastQuery!.Status.ShouldBe("proposed");
        ticketGraph.LastQuery.ExcludeProposed.ShouldBeTrue();

        // The proposed memory is reached, and the manifest records the status it was reached under.
        response.Bundle.Manifest.NoMatch.ShouldBeFalse();
        response.Bundle.Items.ShouldContain(i => i.Uuid == ProposedUuid);
        response.Bundle.Manifest.Selection.RetrievalPolicy.ShouldBe("current-only, status=proposed");

        // And the same anchor without a status still excludes proposed, so the fix did not widen the
        // default: this is the branch the defect lived in, and it must keep behaving as it did. The
        // control needs an approved memory actually in the store, or the assertion would pass for the
        // wrong reason — a no-match caused by the anchor search finding nothing, rather than by the
        // status rule. The seeded LINKS edge makes the exclusion one widening has to apply too: the
        // anchor search drops the proposed row, so the only route left to bring it back is the widened
        // stage, and that stage carried no proposed rule at all.
        var withoutStatus = new FilteringTicketGraph([
            new(ProposedUuid, ProductGroupUuid, "Proposed", MemoryVersion.MemoryVersionStatus.Proposed),
            new(AnchorUuid, ProductGroupUuid, "Anchor", MemoryVersion.MemoryVersionStatus.Approved),
        ]);
        CreateDossierBundle.Handler defaulting = new(
            AppDb, Search, new NpgsqlMemoryTraversal(Db), withoutStatus, Graph, blobs,
            NullLogger<CreateDossierBundle.Handler>.Instance);

        CreateDossierBundle.Response noStatus = await defaulting.Handle(
            BundleRequest(ticketProvider: "jira", ticketKey: "ACM-999"), Ct);

        withoutStatus.LastQuery!.Status.ShouldBeNull();
        noStatus.Bundle.Manifest.NoMatch.ShouldBeFalse();
        noStatus.Bundle.Items.ShouldNotContain(i => i.Uuid == ProposedUuid);
        noStatus.Bundle.Items.ShouldContain(i => i.Uuid == AnchorUuid);
        noStatus.Bundle.Manifest.Selection.RetrievalPolicy.ShouldBe("current-only, proposed-excluded");
    }

    [Fact]
    public async Task Bundle_items_follow_the_provenance_order_not_the_selection_order()
    {
        await SeedForOrderingAsync();
        CreateDossierBundle.Handler handler = BundleHandler(new DictionaryBlobStorage());

        CreateDossierBundle.Response response = await handler.Handle(BundleRequest(includeHistory: true), Ct);

        // The selection orders memories by their current version, so the history row of OrderingFirst
        // (valid long before every other row) would otherwise trail its own current version. The
        // bundle orders the rows themselves: validity, then capture, then identity, then version.
        response.Bundle.Items.Select(i => (i.Uuid, i.Version)).ShouldBe(
        [
            (OrderingFirst, 1),
            (OrderingTieLow, 1),
            (OrderingTieHigh, 1),
            (OrderingLaterCapture, 1),
            (OrderingFirst, 2),
        ]);
        response.Bundle.Items.ShouldBe(
            [.. response.Bundle.Items
                .OrderBy(i => i.ValidFrom)
                .ThenBy(i => i.CreatedOn)
                .ThenBy(i => i.Uuid)
                .ThenBy(i => i.Version)]);
    }

    [Fact]
    public async Task AsOf_excludes_a_memory_whose_validity_starts_after_it_from_every_stage()
    {
        // 02:00 at +02:00 is midnight UTC, and the future rows start at 01:00 UTC. A handler that read
        // the offset as wall-clock time would admit them; one that did not normalise would throw.
        DateTimeOffset asOf = new(2024, 3, 10, 2, 0, 0, TimeSpan.FromHours(2));
        await SeedForAsOfAsync(future: new DateTimeOffset(2024, 3, 10, 1, 0, 0, TimeSpan.Zero));
        CreateDossierBundle.Handler handler = BundleHandler(new DictionaryBlobStorage());

        CreateDossierBundle.Response unbounded = await handler.Handle(BundleRequest(), Ct);
        CreateDossierBundle.Response bounded = await handler.Handle(BundleRequest(asOf: asOf), Ct);

        // Control: without AsOf both future rows are reached — one as an anchor, one by widening.
        unbounded.Bundle.Items.Select(i => i.Uuid).ShouldBe(
            [AnchorUuid, VisibleDeepUuid, FutureAnchorUuid, FutureWidenedUuid], ignoreOrder: true);

        bounded.Bundle.Items.Select(i => i.Uuid).ShouldBe([AnchorUuid, VisibleDeepUuid]);
        bounded.Bundle.Manifest.Reach.Anchors.ShouldBe(1);
        bounded.Bundle.Manifest.Reach.Widened.ShouldBe(1);
        bounded.Bundle.Manifest.Reach.Selected.ShouldBe(2);
        bounded.Bundle.Edges.Select(e => e.TargetUuid).ShouldBe([VisibleDeepUuid]);
        bounded.Bundle.Manifest.Selection.AsOf.ShouldBe(asOf);
    }

    [Fact]
    public async Task NonZero_history_inflated_cut_converges_preview_and_bundle()
    {
        // The truncated-empty path already agreed before this change (PR #124 gave the bundle the
        // same disclosure call for a zero-match selection). The path that actually diverged — and
        // this fix changes by deleting two bundle-only disjuncts — is the non-zero one: many version
        // rows of a few selected memories inflate the item count past the anchor limit, producing a
        // cut the blob-free preview cannot predict. Assert the two slices agree here too, so a future
        // re-add of a bundle-only disjunct fails instead of slipping through a green suite.
        await SeedHistoryInflatedBundleAsync();
        var blobs = new DictionaryBlobStorage();
        CreateDossierBundle.Handler bundle = BundleHandler(blobs);
        CreateDossierPreview.Handler preview = PreviewHandler();

        CreateDossierBundle.Response bundleResponse = await bundle.Handle(BundleRequest(includeHistory: true), Ct);
        CreateDossierPreview.Response previewResponse = await preview.Handle(PreviewRequest(includeHistory: true), Ct);

        bundleResponse.Bundle.Items.Count.ShouldBe(DossierDefaults.ItemLimit);
        bundleResponse.Bundle.Omitted.ShouldContain(o => o.Reason == DossierOmissionReason.CapReached);
        // The history-inflated cut is disclosed per-item; the manifest does NOT re-report it (the
        // preview could not predict it). This is what pins the convergence on the non-zero path.
        bundleResponse.Bundle.Manifest.LimitsHit.ShouldNotContain(l => l.Limit == DossierOmissionReason.CapReached);
        previewResponse.LimitsHit.ShouldBe(bundleResponse.Bundle.Manifest.LimitsHit);
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
        // restart: no in-process caching, no change-tracker or handler-scoped state can leak into the bytes.
        // Both contexts share one process, so string hash randomization (per-process) is NOT exercised here;
        // cross-process byte stability rests on the fully deterministic orderings (validity, capture, identity).
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

    private static CreateDossierBundle.Request BundleRequest(
        IReadOnlyList<string>? tags = null, string? ticketProvider = null, string? ticketKey = null,
        bool includeHistory = false, string? status = null, DateTimeOffset? asOf = null) =>
        new(
            Repo: "kingstown",
            InitiativeName: null,
            TicketProvider: ticketProvider,
            TicketKey: ticketKey,
            Tags: tags ?? ["tag-1"],
            Kind: null,
            Status: status,
            ScopeDimension: null,
            IncludeHistory: includeHistory,
            AsOf: asOf,
            WidenDepth: 3);

    private static CreateDossierPreview.Request PreviewRequest(
        IReadOnlyList<string>? tags = null, string? ticketProvider = null, string? ticketKey = null,
        bool includeHistory = false) =>
        new(
            Repo: "kingstown",
            InitiativeName: null,
            TicketProvider: ticketProvider,
            TicketKey: ticketKey,
            Tags: tags ?? ["tag-1"],
            Kind: null,
            Status: null,
            ScopeDimension: null,
            IncludeHistory: includeHistory,
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

    private async Task SeedForProposedBundleAsync()
    {
        MemoryGroup product = Group(ProductGroupUuid, MemoryGroup.ScopeDimensionValue.Product);
        Db.MemoryGroups.Add(product);
        await Db.SaveChangesAsync(Ct);

        // One proposed and one approved memory, because the assertion needs both: the proposed row is
        // what a status=proposed anchor must reach, and the approved row is the control proving the
        // default still excludes proposed rather than the selection simply coming back empty.
        Memory proposed = MemoryRow(product.Id, ProposedUuid, "Proposed", "A proposed fact", tags: ["tag-1"],
            facet: "architecture");
        Memory approved = MemoryRow(product.Id, AnchorUuid, "Anchor", "Anchor fact", tags: ["tag-1"],
            facet: "architecture");
        Db.Memories.AddRange(proposed, approved);
        await Db.SaveChangesAsync(Ct);

        Db.MemoryVersions.Add(Version(proposed.Id, 1, "A proposed claim", isCurrent: true,
            kind: MemoryVersion.KindValue.Decision, status: MemoryVersion.MemoryVersionStatus.Proposed));
        Db.MemoryVersions.Add(Version(approved.Id, 1, "Anchor claim", isCurrent: true,
            kind: MemoryVersion.KindValue.Decision));
        await Db.SaveChangesAsync(Ct);

        // The edge is what makes this a test of the proposed rule at all. Without it the proposed
        // memory is only ever reachable as a ticket identity, so widening runs, finds nothing, and the
        // control below would assert the exclusion of a memory widening could not have returned anyway
        // — green with the defect present. Anchored on the approved memory, the no-status control now
        // reaches the proposed one by widening, which is the route the exclusion has to hold on.
        (await Graph.CreateAsync(AnchorUuid, ProposedUuid, MemoryRelation.DependsOn, "not yet settled", Ct))
            .ShouldBeTrue();
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

    private async Task SeedForOrderingAsync()
    {
        MemoryGroup product = Group(ProductGroupUuid, MemoryGroup.ScopeDimensionValue.Product);
        Db.MemoryGroups.Add(product);
        await Db.SaveChangesAsync(Ct);

        // Inserted against the expected order, so neither insertion nor id order can pass for it.
        Memory first = MemoryRow(product.Id, OrderingFirst, "First", "History first", tags: ["tag-1"], facet: "architecture");
        Memory tieHigh = MemoryRow(product.Id, OrderingTieHigh, "Tie high", "Tie high", tags: ["tag-1"], facet: "architecture");
        Memory laterCapture = MemoryRow(product.Id, OrderingLaterCapture, "Later", "Later capture", tags: ["tag-1"], facet: "architecture");
        Memory tieLow = MemoryRow(product.Id, OrderingTieLow, "Tie low", "Tie low", tags: ["tag-1"], facet: "architecture");
        Db.Memories.AddRange(first, tieHigh, laterCapture, tieLow);
        await Db.SaveChangesAsync(Ct);

        Db.MemoryVersions.Add(Version(first.Id, 1, "First v1", isCurrent: false, kind: MemoryVersion.KindValue.Decision,
            validFrom: ValidFrom.AddDays(-10)));
        Db.MemoryVersions.Add(Version(first.Id, 2, "First v2", isCurrent: true, kind: MemoryVersion.KindValue.Decision,
            validFrom: ValidFrom.AddDays(5)));
        Db.MemoryVersions.Add(Version(tieHigh.Id, 1, "Tie high", isCurrent: true, kind: MemoryVersion.KindValue.Decision));
        Db.MemoryVersions.Add(Version(laterCapture.Id, 1, "Later", isCurrent: true, kind: MemoryVersion.KindValue.Decision,
            createdOn: CreatedOn.AddHours(1)));
        Db.MemoryVersions.Add(Version(tieLow.Id, 1, "Tie low", isCurrent: true, kind: MemoryVersion.KindValue.Decision));
        await Db.SaveChangesAsync(Ct);
    }

    private async Task SeedForAsOfAsync(DateTimeOffset future)
    {
        MemoryGroup product = Group(ProductGroupUuid, MemoryGroup.ScopeDimensionValue.Product);
        Db.MemoryGroups.Add(product);
        await Db.SaveChangesAsync(Ct);

        Memory anchor = MemoryRow(product.Id, AnchorUuid, "Anchor", "Anchor fact", tags: ["tag-1"], facet: "architecture");
        Memory widened = MemoryRow(product.Id, VisibleDeepUuid, "Widened", "Widened fact", tags: ["other"], facet: "architecture");
        Memory futureAnchor = MemoryRow(product.Id, FutureAnchorUuid, "Future anchor", "Future anchor fact", tags: ["tag-1"],
            facet: "architecture");
        Memory futureWidened = MemoryRow(product.Id, FutureWidenedUuid, "Future widened", "Future widened fact",
            tags: ["other"], facet: "architecture");
        Db.Memories.AddRange(anchor, widened, futureAnchor, futureWidened);
        await Db.SaveChangesAsync(Ct);

        Db.MemoryVersions.Add(Version(anchor.Id, 1, "Anchor claim", isCurrent: true, kind: MemoryVersion.KindValue.Decision));
        Db.MemoryVersions.Add(Version(widened.Id, 1, "Widened claim", isCurrent: true, kind: MemoryVersion.KindValue.Decision));
        Db.MemoryVersions.Add(Version(futureAnchor.Id, 1, "Future anchor claim", isCurrent: true,
            kind: MemoryVersion.KindValue.Decision, validFrom: future));
        Db.MemoryVersions.Add(Version(futureWidened.Id, 1, "Future widened claim", isCurrent: true,
            kind: MemoryVersion.KindValue.Decision, validFrom: future));
        await Db.SaveChangesAsync(Ct);

        (await Graph.CreateAsync(AnchorUuid, VisibleDeepUuid, MemoryRelation.DependsOn, "needs it", Ct)).ShouldBeTrue();
        (await Graph.CreateAsync(AnchorUuid, FutureWidenedUuid, MemoryRelation.DependsOn, "later", Ct)).ShouldBeTrue();
    }

    private async Task SeedHistoryInflatedBundleAsync()
    {
        MemoryGroup product = Group(ProductGroupUuid, MemoryGroup.ScopeDimensionValue.Product);
        Db.MemoryGroups.Add(product);
        await Db.SaveChangesAsync(Ct);

        Memory anchor = MemoryRow(product.Id, AnchorUuid, "Anchor", "Anchor fact", tags: ["tag-1"], facet: "architecture");
        Db.Memories.Add(anchor);
        await Db.SaveChangesAsync(Ct);

        // One memory with enough versions that IncludeHistory inflates the bundle's item count past
        // the anchor limit, producing a history-inflated cut even though the selected memory count
        // stays far below the fetch ceiling (so LimitReached stays false — that is the divergence this
        // fix removes).
        for (int version = 1; version <= DossierDefaults.ItemLimit + 1; version++)
        {
            Db.MemoryVersions.Add(Version(
                anchor.Id, version, $"Anchor claim {version}",
                isCurrent: version == DossierDefaults.ItemLimit + 1,
                kind: MemoryVersion.KindValue.Decision, blobAddress: null));
        }

        await Db.SaveChangesAsync(Ct);
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
        long memoryId, int version, string statement, bool isCurrent, string kind, string? blobAddress = null,
        string status = MemoryVersion.MemoryVersionStatus.Approved, DateTimeOffset? validFrom = null,
        DateTimeOffset? createdOn = null) => new()
        {
            MemoryId = memoryId,
            Version = version,
            IsCurrent = isCurrent,
            Statement = statement,
            ContentSummary = $"Summary of {statement}",
            BlobAddress = blobAddress,
            Kind = kind,
            Confidence = 80,
            Status = status,
            Sources = [SourceDocument.Create("jira", "ACM-1", ValidFrom)],
            ValidFrom = validFrom ?? ValidFrom,
            CreatedOn = createdOn ?? CreatedOn,
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

    private sealed class FakeTicketGraph(TicketTraversalResult result) : ITicketGraph
    {
        public Task LockAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> ChangeParentAsync(TicketParentChange change, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<TicketTraversalResult> TraverseAsync(TicketTraversalQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed record TicketCandidate(Guid Uuid, Guid GroupUuid, string Name, string Status);

    /// <summary>
    /// A ticket graph that filters its candidates by the query the way the real one does, rather than
    /// returning a fixed result. Mirrors the predicate at NpgsqlTicketGraph.Traversal.cs:116-117 —
    /// <c>((@status IS NOT NULL AND status = @status) OR (@status IS NULL AND (NOT @exclude_proposed OR
    /// status &lt;&gt; 'proposed')))</c> — so a caller that forgets to forward a field is reproduced here
    /// as the empty identity set it is in production, instead of being masked by a cooperative fake.
    /// </summary>
    private sealed class FilteringTicketGraph(IReadOnlyList<TicketCandidate> candidates) : ITicketGraph
    {
        public TicketTraversalQuery? LastQuery { get; private set; }

        public IReadOnlyList<CheapMemory> LastItems { get; private set; } = [];

        public Task LockAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> ChangeParentAsync(TicketParentChange change, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<TicketTraversalResult> TraverseAsync(
            TicketTraversalQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;

            var kept = candidates
                .Where(c => query.Status is { Length: > 0 } status
                    ? c.Status == status
                    : !query.ExcludeProposed || c.Status != MemoryVersion.MemoryVersionStatus.Proposed)
                .Select(c => new CheapMemory(
                    c.Uuid, c.GroupUuid, c.Name, c.Name, "claim", "summary", "decision",
                    ["architecture"], ["tag-1"], c.Status, 80, MemoryGroup.ScopeDimensionValue.Product,
                    null, ValidFrom, null, 1, true, [SourceDocument.Create("jira", "ACM-999", ValidFrom)],
                    CreatedOn))
                .ToList();
            LastItems = kept;

            return Task.FromResult(new TicketTraversalResult(
                Paths: [],
                Items: kept,
                Disclosure: new TicketTraversalDisclosure(
                    query.MaxDepth, query.PathLimit, query.MemoryLimit, false, false, false)));
        }
    }
}
