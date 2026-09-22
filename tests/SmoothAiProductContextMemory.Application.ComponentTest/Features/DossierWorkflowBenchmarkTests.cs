using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Features.ContextDossier;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

/// <summary>
/// NFR-03 evidence: the numeric item/latency limits for the contextual-export read, derived from the
/// three reference workflows (spec preparation, handover, re-entry) rather than assumed (BRD-002 §7,
/// HLD-005 NFR-03 §"Numeric limits — provisional, pending validation").
/// </summary>
/// <remarks>
/// Env-gated because it seeds a representative store and times real bundle/preview handlers; the output
/// is measurement evidence recorded to the HLD folder, not a pass/fail the PR gate needs on every push.
/// Run with
/// <c>SMOOTH_DOSSIER_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Application.ComponentTest
/// --filter DossierWorkflowBenchmarkTests</c>.
/// <para>
/// Each workflow encodes a representative request shape and records slice size (selected / widened /
/// edges), whether the provisional <see cref="DossierDefaults.ItemLimit"/> (the reachable bound) was
/// reached, the
/// on-the-wire payload size, and preview + bundle latency. The recorded values <b>are</b> the evidence
/// that sets the limits; nothing here asserts an assumed cap (that would cite a placeholder as a
/// specification, which NFR-03 forbids). Composition usage and whether the document was fit for its task
/// are the dossier skill's judgement (NFR-07, the reference-example review) and are measured at the
/// skill level, not here — this harness records the slice each workflow produces so the composition side
/// has a size to reason about.
/// </para>
/// </remarks>
public sealed class DossierWorkflowBenchmarkTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    private const int MemoryCount = 620;
    private const int ProgramEvery = 7;          // g % 7 == 0 -> programme-scoped (hidden to a product caller)
    private const int UnderstandingEvery = 10;   // g % 10 == 0 -> kind = understanding
    private const int HistoryEvery = 5;          // g % 5 == 0 -> a further non-current version (history)
    private const int EdgeCount = 1_400;
    private const int WarmupIterations = 10;
    private const int MeasuredIterations = 30;

    // The provisional cap is what the reference workflows are being measured against. It is recorded per
    // export by the bundle itself; it is NOT asserted here as a specification (NFR-03).
    private const int ProvisionalItemLimit = DossierDefaults.ItemLimit;

    private CreateDossierBundle.Handler BundleHandler(IBlobStorage blobs) =>
        new(AppDb, Search, new NpgsqlMemoryTraversal(Db), new NpgsqlTicketGraph(Db), Graph, blobs,
            NullLogger<CreateDossierBundle.Handler>.Instance);

    private CreateDossierPreview.Handler PreviewHandler() =>
        new(Search, new NpgsqlMemoryTraversal(Db), new NpgsqlTicketGraph(Db), Graph,
            NullLogger<CreateDossierPreview.Handler>.Instance);

    [Fact]
    public async Task Reference_workflows_record_slice_size_fit_and_latency()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable("SMOOTH_DOSSIER_BENCH"), "1", StringComparison.Ordinal),
            "Set SMOOTH_DOSSIER_BENCH=1 to record the HLD-005 NFR-03 reference-workflow measurements.");

        var report = new StringBuilder();
        Stopwatch seedTimer = Stopwatch.StartNew();
        await SeedAsync();
        seedTimer.Stop();
        report.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"seed: {MemoryCount} memories, {EdgeCount} edges, kind=understanding every {UnderstandingEvery}, "
            + $"history version every {HistoryEvery}, programme-scoped every {ProgramEvery}, in {seedTimer.Elapsed.TotalSeconds:F1}s"));

        var blobs = new CountingBlobStorage();
        CreateDossierPreview.Handler preview = PreviewHandler();
        CreateDossierBundle.Handler bundle = BundleHandler(blobs);

        report.AppendLine();
        report.AppendLine("Each workflow recorded against the provisional item limit "
            + $"({ProvisionalItemLimit}). Reported values are the evidence; they are not a specification.");
        report.AppendLine();
        report.AppendLine("| Workflow | Request shape | Selected | Widened | Edges | Item limit hit? | Blob reads | Payload (KiB) |");
        report.AppendLine("|---|---|---|---|---|---|---|---|");

        var rows = new List<(string Workflow, WorkflowRow Row)>();

        foreach (WorkflowSpec workflow in Workflows())
        {
            WorkflowRow row = await RunWorkflowAsync(preview, bundle, workflow, blobs, Ct);
            rows.Add((workflow.Name, row));

            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {workflow.Name} | {workflow.Shape} | {row.Selected} | {row.Widened} | {row.Edges} | "
                + $"{(row.ItemLimitHit ? "YES (" + (row.ItemLimitHitValue is int v ? v.ToString(CultureInfo.InvariantCulture) : "?") + ")" : "no")} "
                + $"| {row.BlobReads} | {row.PayloadKiB:F1} |"));
        }

        report.AppendLine();
        report.AppendLine("| Workflow | Preview p50 (ms) | Preview p95 (ms) | Bundle p50 (ms) | Bundle p95 (ms) |");
        report.AppendLine("|---|---|---|---|---|");
        foreach ((string workflow, WorkflowRow row) in rows)
        {
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {workflow} | {row.PreviewP50:F3} | {row.PreviewP95:F3} | {row.BundleP50:F3} | {row.BundleP95:F3} |"));
        }

        report.AppendLine();
        report.AppendLine("### Observations (recorded, not asserted)");
        report.AppendLine();
        foreach ((string workflow, WorkflowRow row) in rows)
        {
            string fit = row.ItemLimitHit
                ? $"the provisional {ProvisionalItemLimit} item cap was REACHED, so the slice was cut at {row.ItemLimitHitValue}."
                : "the slice fit within the provisional item limit (no cut). "
                  + "Whether the document was fit for its task — fidelity of its conditions and exceptions — is "
                  + "assessed separately by the reference-example review (NFR-07), not settled by size alone.";
            report.AppendLine($"- {workflow}: {row.Selected} items selected after widening; {fit} "
                + $"The selection read {row.BlobReads} blob(s).");
        }

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());

        // The only assertions are invariants that hold regardless of the measured numbers: the
        // reconciliation starts from a trustworthy left-hand side, and a no-match is not what a
        // populated workflow produced. No cap is asserted, so these stay evidence.
        foreach ((string workflow, WorkflowRow row) in rows)
        {
            row.ManifestCount.ShouldBe(row.BundleItems + row.Omitted,
                $"{workflow}: the manifest count must equal items present plus omitted-with-reason (NFR-04).");
            row.NoMatch.ShouldBeFalse($"{workflow}: a populated workflow must not report a no-match.");
        }
    }

    private static async Task<WorkflowRow> RunWorkflowAsync(
        CreateDossierPreview.Handler preview,
        CreateDossierBundle.Handler bundle,
        WorkflowSpec workflow,
        IBlobStorage blobs,
        CancellationToken ct)
    {
        CreateDossierPreview.Request previewReq = workflow.PreviewRequest;
        CreateDossierBundle.Request bundleReq = workflow.BundleRequest;

        CreateDossierPreview.Response p = await preview.Handle(previewReq, ct);
        CreateDossierBundle.Response b = await bundle.Handle(bundleReq, ct);

        string payload = JsonSerializer.Serialize(
            b, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        double payloadKiB = Encoding.UTF8.GetBytes(payload).Length / 1024.0;

        int blobReads = blobs is CountingBlobStorage counting ? counting.Reads : 0;

        // Measure the handlers themselves, warmed first.
        Measurement previewMeasurement = await MeasureAsync(() => preview.Handle(previewReq, ct));
        Measurement bundleMeasurement = await MeasureAsync(() => bundle.Handle(bundleReq, ct));

        bool itemLimitHit = b.Bundle.Manifest.LimitsHit.Any(l => l.Limit == DossierOmissionReason.CapReached);
        int? hitValue = b.Bundle.Manifest.LimitsHit
            .FirstOrDefault(l => l.Limit == DossierOmissionReason.CapReached)?.Value;

        return new WorkflowRow(
            Selected: p.Volume.Selected,
            Widened: p.Volume.Widened,
            Edges: p.Volume.Edges,
            ItemLimitHit: itemLimitHit,
            ItemLimitHitValue: hitValue,
            BlobReads: blobReads,
            ManifestCount: b.Bundle.Manifest.SelectedCount,
            BundleItems: b.Bundle.Items.Count,
            Omitted: b.Bundle.Omitted.Count,
            NoMatch: b.Bundle.Manifest.NoMatch,
            PayloadKiB: payloadKiB,
            PreviewP50: previewMeasurement.P50Ms,
            PreviewP95: previewMeasurement.P95Ms,
            BundleP50: bundleMeasurement.P50Ms,
            BundleP95: bundleMeasurement.P95Ms);
    }

    private static async Task<Measurement> MeasureAsync<T>(Func<ValueTask<T>> run)
    {
        for (int i = 0; i < WarmupIterations; i++)
        {
            await run();
        }

        var samples = new double[MeasuredIterations];
        var sw = new Stopwatch();
        for (int i = 0; i < MeasuredIterations; i++)
        {
            sw.Restart();
            await run();
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        return new Measurement(Percentile(samples, 0.50), Percentile(samples, 0.95));
    }

    private static double Percentile(double[] sortedAscending, double p)
    {
        double index = p * (sortedAscending.Length - 1);
        int lo = (int)Math.Floor(index);
        int hi = (int)Math.Ceiling(index);
        return lo == hi
            ? sortedAscending[lo]
            : (sortedAscending[lo] * (1 - (index - lo))) + (sortedAscending[hi] * (index - lo));
    }

    private WorkflowSpec[] Workflows() =>
    [
        // Spec preparation: a practitioner assembling the knowledge for a specification. Narrow and
        // bounded — the spec-relevant decisions, widened a couple of hops for their immediate context.
        new("spec preparation", "repo + tag=spec, depth 2, decision kind",
            new CreateDossierPreview.Request(Repo: "kingstown", InitiativeName: null, TicketProvider: null,
                TicketKey: null, Tags: ["spec"], Kind: "decision", Status: null, ScopeDimension: null,
                IncludeHistory: false, AsOf: null, WidenDepth: 2),
            new CreateDossierBundle.Request(Repo: "kingstown", InitiativeName: null, TicketProvider: null,
                TicketKey: null, Tags: ["spec"], Kind: "decision", Status: null, ScopeDimension: null,
                IncludeHistory: false, AsOf: null, WidenDepth: 2)),
        // Handover: a practitioner transferring a whole body of work. Broad, deep widening, history
        // included. This is the workflow most likely to exceed the provisional cap.
        new("handover", "repo, depth 5, all kinds, history",
            new CreateDossierPreview.Request(Repo: "kingstown", InitiativeName: null, TicketProvider: null,
                TicketKey: null, Tags: null, Kind: null, Status: null, ScopeDimension: null,
                IncludeHistory: true, AsOf: null, WidenDepth: 5),
            new CreateDossierBundle.Request(Repo: "kingstown", InitiativeName: null, TicketProvider: null,
                TicketKey: null, Tags: null, Kind: null, Status: null, ScopeDimension: null,
                IncludeHistory: true, AsOf: null, WidenDepth: 5)),
        // Re-entry: a practitioner returning to a capability after a gap. Anchored on a capability tag,
        // decision kind, moderate depth — the middle size between spec and handover.
        new("re-entry", "repo + tag=re-entry, depth 3, decision kind",
            new CreateDossierPreview.Request(Repo: "kingstown", InitiativeName: null, TicketProvider: null,
                TicketKey: null, Tags: ["re-entry"], Kind: "decision", Status: null, ScopeDimension: null,
                IncludeHistory: false, AsOf: null, WidenDepth: 3),
            new CreateDossierBundle.Request(Repo: "kingstown", InitiativeName: null, TicketProvider: null,
                TicketKey: null, Tags: ["re-entry"], Kind: "decision", Status: null, ScopeDimension: null,
                IncludeHistory: false, AsOf: null, WidenDepth: 3)),
    ];

    private async Task SeedAsync()
    {
        // One product group and one programme group. Programme is the hidden dimension for a
        // product/null-scope caller, so the benchmark exercises scope gating on widening too.
        var productGroup = new MemoryGroup
        {
            Uuid = Guid.NewGuid(),
            ScopeDimension = MemoryGroup.ScopeDimensionValue.Product,
            InitiativeId = 1,
            Repo = "kingstown",
            CreatedOn = DateTimeOffset.UtcNow.AddDays(-60),
        };
        var programGroup = new MemoryGroup
        {
            Uuid = Guid.NewGuid(),
            ScopeDimension = MemoryGroup.ScopeDimensionValue.Program,
            InitiativeId = 1,
            Repo = "kingstown",
            CreatedOn = DateTimeOffset.UtcNow.AddDays(-60),
        };
        Db.MemoryGroups.AddRange(productGroup, programGroup);
        await Db.SaveChangesAsync(Ct);

        var memories = new List<Memory>(MemoryCount);
        for (int g = 1; g <= MemoryCount; g++)
        {
            var group = g % ProgramEvery == 0 ? programGroup : productGroup;
            memories.Add(new Memory
            {
                Uuid = Guid.NewGuid(),
                LineageId = Guid.NewGuid(),
                GroupId = group.Id,
                Name = $"wf-{g}",
                Description = $"workflow subject {g}",
                SubjectSlug = $"wf-subject-{g}",
                Tags =
                [
                    g % 3 == 1 ? "spec" : g % 3 == 2 ? "handover" : "re-entry",
                    "capability",
                    g % 10 == 0 ? "architecture" : "api",
                ],
                Facets = [g % 8 == 0 ? "security" : "behaviour"],
            });
        }

        Db.Memories.AddRange(memories);
        await Db.SaveChangesAsync(Ct);

        DateTimeOffset validFrom = DateTimeOffset.UtcNow.AddDays(-30);
        DateTimeOffset createdOn = DateTimeOffset.UtcNow.AddDays(-40);
        foreach (Memory m in memories)
        {
            Db.MemoryVersions.Add(Version(m.Id, 1, isCurrent: true, kind: KindOf(m),
                status: MemoryVersion.MemoryVersionStatus.Approved, validFrom: validFrom, createdOn: createdOn));

            if (m.Id % HistoryEvery == 0)
            {
                Db.MemoryVersions.Add(Version(m.Id, 0, isCurrent: false, kind: KindOf(m),
                    status: MemoryVersion.MemoryVersionStatus.Approved,
                    validFrom: validFrom.AddDays(-60), createdOn: createdOn.AddDays(-50)));
            }
        }

        await Db.SaveChangesAsync(Ct);

        for (int g = 0; g < EdgeCount; g++)
        {
            Memory source = memories[g % MemoryCount];
            Memory target = memories[((g + 1 + (g / MemoryCount)) % MemoryCount)];
            string relation = g % 4 == 0 ? MemoryRelation.DependsOn
                : g % 5 == 0 ? MemoryRelation.Implements
                : g % 2 == 0 ? MemoryRelation.RelatesTo
                : MemoryRelation.Supersedes;
            await Graph.CreateAsync(source.Uuid, target.Uuid, relation, $"workflow seed {g}", Ct);
        }
    }

    private static string KindOf(Memory m) =>
        m.Id % UnderstandingEvery == 0 ? MemoryVersion.KindValue.Understanding
        : m.Id % 4 == 0 ? MemoryVersion.KindValue.Decision
        : MemoryVersion.KindValue.Reference;

    private static MemoryVersion Version(
        long memoryId, int version, bool isCurrent, string kind, string status,
        DateTimeOffset validFrom, DateTimeOffset createdOn) => new()
    {
        MemoryId = memoryId,
        Version = version,
        IsCurrent = isCurrent,
        Statement = $"workflow {(isCurrent ? "claim" : "prior claim")} {memoryId}",
        ContentSummary = $"workflow {(isCurrent ? "summary" : "prior summary")} {memoryId}",
        BlobAddress = null,
        Kind = kind,
        Confidence = isCurrent ? (short)80 : (short)70,
        Status = status,
        Sources = [SourceDocument.Create("jira", "ACM-1", validFrom)],
        ValidFrom = validFrom,
        CreatedOn = createdOn,
    };

    private sealed record WorkflowSpec(
        string Name,
        string Shape,
        CreateDossierPreview.Request PreviewRequest,
        CreateDossierBundle.Request BundleRequest);

    private sealed record WorkflowRow(
        int Selected,
        int Widened,
        int Edges,
        bool ItemLimitHit,
        int? ItemLimitHitValue,
        int BlobReads,
        int ManifestCount,
        int BundleItems,
        int Omitted,
        bool NoMatch,
        double PayloadKiB,
        double PreviewP50,
        double PreviewP95,
        double BundleP50,
        double BundleP95);

    private readonly record struct Measurement(double P50Ms, double P95Ms);

    /// <summary>
    /// Blob storage that serves no object — the seeded references carry no blob address, so hydration
    /// yields the <c>none</c> body state. It counts reads so the benchmark can record that the bundle
    /// read no blobs for an all-inline slice (NFR-03: the preview already reads none by structure).
    /// </summary>
    private sealed class CountingBlobStorage : IBlobStorage
    {
        private int _reads;

        public int Reads => _reads;

        public Task<string> StoreAsync(Stream content, string? contentType = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The dossier benchmark reads only.");

        public Task<BlobContent?> GetAsync(string address, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult<BlobContent?>(null);
        }

        public Task<bool> ExistsAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
