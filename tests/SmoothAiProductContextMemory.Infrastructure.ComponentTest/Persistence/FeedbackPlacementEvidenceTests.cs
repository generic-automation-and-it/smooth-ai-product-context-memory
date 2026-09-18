using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// Evidence harness deciding where recall feedback lives (HLD-004 LADR-02): prototypes the counter
/// column (A) and the append-only record set (B) against the unchanged read path as the
/// telemetry-derived baseline (C), and measures the discriminators the decision turns on.
/// </summary>
/// <remarks>
/// Env-gated because it is evidence recorded to the HLD folder, not a pass/fail the PR gate needs on
/// every push. Run with <c>SMOOTH_FEEDBACK_BENCH=1 dotnet test
/// tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest --filter
/// FeedbackPlacementEvidenceTests</c>.
/// <para>
/// Prototype objects are created only inside this test's isolated database; the production schema is
/// untouched, exactly as <see cref="RecallTuningEvidenceTests"/> creates its candidate indexes. No
/// production feedback mechanism exists yet, and this harness must not become one — it exists to settle
/// the placement, after which the shipped write path is built against the chosen shape.
/// </para>
/// <para>
/// Wall clock alone cannot settle LADR-02: at any volume a single indexed write is fast, and the
/// question is whether the write <em>serialises</em>. The decisive measurements are therefore
/// deterministic rather than statistical — a second connection attempting the same write under a
/// <c>lock_timeout</c>, the <c>ctid</c> of the recalled row before and after, and whether the mechanism
/// can physically hold a miss or a recency at all. Latency is still measured over rotated repeat rounds,
/// because a gap between two placements means nothing without the run-to-run spread of one of them.
/// </para>
/// </remarks>
public sealed class FeedbackPlacementEvidenceTests : PersistenceTestBase
{
    private const int MemoryCount = 3_000;

    /// <summary>
    /// Memories captured "now" rather than 30 days ago, so the never-recalled measure has something to
    /// exclude. Without this split the NFR-03 criterion is untestable: a corpus seeded at one instant is
    /// either entirely recent or entirely old.
    /// </summary>
    private const int RecentlyCapturedCount = 50;

    private const int RecentlyCapturedGraceDays = 7;
    private const int MissRateWindowDays = 7;

    private const int WarmupIterations = 20;
    private const int MeasuredIterations = 100;

    /// <summary>
    /// Each placement is measured in several independent rounds, and the placements are rotated within
    /// each round. One round per placement cannot say whether a sub-millisecond gap between two of them
    /// is a property or noise, and a fixed order makes whichever placement runs last measure against a
    /// larger feedback table and a dirtier <c>memory</c> table than the first.
    /// </summary>
    private const int MeasurementRounds = 3;

    /// <summary>The retention bound the growth projection is read against (LADR-02).</summary>
    private const int RetentionDays = 30;

    private const long RetentionRecordCap = 2_000_000;

    private const int ConcurrentWorkers = 16;
    private const int OperationsPerWorker = 20;
    private const int DirtyingIterations = 200;
    private const int RecalledSubsetSize = 10;
    private const int MissCount = 7;

    /// <summary>
    /// Absolute per-retrieval budget for the feedback write, deliberately generous. A ratio against the
    /// baseline would fail on scheduler noise at sub-millisecond magnitudes, and a guard that cries wolf
    /// gets deleted rather than investigated; a serialising write exceeds this by orders of magnitude.
    /// </summary>
    private const double WriteOverheadBudgetMs = 2.0;

    /// <summary>
    /// The bounded retrieval-shape set (LADR-03). Present only to prove a <c>CHECK</c> constraint rejects
    /// anything outside it; the shipped set belongs with the writer that emits it.
    /// </summary>
    private static readonly string[] RetrievalShapes =
        ["free_text", "facet_only", "ticket_scoped", "group_scoped", "unfiltered"];

    public FeedbackPlacementEvidenceTests(AspireFixture aspire) : base(aspire) { }

    private NpgsqlMemorySearch Search => new(Db);

    [Fact]
    public async Task Placement_discriminators_for_counter_append_and_telemetry()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable("SMOOTH_FEEDBACK_BENCH"), "1", StringComparison.Ordinal),
            "Set SMOOTH_FEEDBACK_BENCH=1 to record the LADR-02 placement measurements.");

        var report = new StringBuilder();

        Stopwatch seedTimer = Stopwatch.StartNew();
        await SeedAsync();
        await CreatePrototypeObjectsAsync();
        seedTimer.Stop();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"seed: {MemoryCount} memories ({RecentlyCapturedCount} captured now, the rest 30 days ago) "
            + $"plus prototype objects in {seedTimer.Elapsed.TotalSeconds:F1}s");

        var broad = new MemorySearchCriteria { FreeText = "bench" };
        var hot = new MemorySearchCriteria { FreeText = "singular" };

        IReadOnlyList<CheapMemory> broadRows = await Search.SearchAsync(broad, Ct);
        IReadOnlyList<CheapMemory> hotRows = await Search.SearchAsync(hot, Ct);
        report.AppendLine(CultureInfo.InvariantCulture,
            $"query shapes: broad returns {broadRows.Count} rows (the write amplification of one retrieval), "
            + $"hot returns {hotRows.Count} row (the contention shape)");

        broadRows.Count.ShouldBeGreaterThan(1, "the latency shape must amplify, or the write cost is understated");
        hotRows.Count.ShouldBe(1, "the contention shape must resolve to exactly one popular memory");
        Guid hotUuid = hotRows[0].Uuid;

        TriggerAudit triggers = await AuditTriggersAsync();
        AppendTriggerAudit(report, triggers);

        bool shapeConstrained = await ShapeColumnRejectsFreeTextAsync();
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"bounded classification: an out-of-set shape value is "
            + $"{(shapeConstrained ? "rejected" : "ACCEPTED")} by the prototype constraint");

        ReadPathImpact impact = await MeasureReadPathImpactAsync(broad, broadRows);
        AppendReadPathImpact(report, impact);

        IReadOnlyList<Measurement> latency = await MeasureLatencyAsync(
        [
            ("C — telemetry-derived (no store writes)", async () => await Search.SearchAsync(broad, Ct)),
            ("A — counter on the memory row", async () =>
            {
                IReadOnlyList<CheapMemory> rows = await Search.SearchAsync(broad, Ct);
                await WriteCounterAsync(rows);
            }),
            ("B — append-only records", async () =>
            {
                IReadOnlyList<CheapMemory> rows = await Search.SearchAsync(broad, Ct);
                await WriteAppendAsync(rows, RetrievalShapes[0]);
            }),
        ]);

        Measurement baseline = latency[0];
        Measurement counter = latency[1];
        Measurement appended = latency[2];

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"### retrieval latency, broad shape — {MeasurementRounds} rotated rounds of "
            + $"{MeasuredIterations} iterations after {WarmupIterations} warmups");
        report.AppendLine();
        report.AppendLine("| Placement | p50 (ms) | p95 (ms) | p95 delta vs C (ms) | p95 across rounds (ms) | run-to-run spread (ms) |");
        report.AppendLine("|---|---|---|---|---|---|");
        report.AppendLine(LatencyRow(baseline, baseline.P95Ms));
        report.AppendLine(LatencyRow(counter, baseline.P95Ms));
        report.AppendLine(LatencyRow(appended, baseline.P95Ms));

        double placementGapMs = Math.Abs(counter.P95Ms - appended.P95Ms);
        double widestSpreadMs = Math.Max(counter.P95SpreadMs, appended.P95SpreadMs);
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"A↔B p95 gap {placementGapMs:F3} ms against the widest single-placement run-to-run p95 spread "
            + $"{widestSpreadMs:F3} ms — latency "
            + $"{(placementGapMs > widestSpreadMs ? "separates A from B" : "does not separate A from B")}.");

        Concurrency counterConcurrency = await MeasureConcurrencyAsync(
            "A — counter on the memory row", hot, WriteCounterAsync);
        Concurrency appendConcurrency = await MeasureConcurrencyAsync(
            "B — append-only records", hot, rows => WriteAppendAsync(rows, RetrievalShapes[0]));

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"### concurrency on one popular memory — {ConcurrentWorkers} workers x {OperationsPerWorker} retrievals");
        report.AppendLine();
        report.AppendLine("| Placement | p50 (ms) | p95 (ms) | wall clock (s) | throughput (ops/s) |");
        report.AppendLine("|---|---|---|---|---|");
        report.AppendLine(ConcurrencyRow(counterConcurrency));
        report.AppendLine(ConcurrencyRow(appendConcurrency));

        LockProbe counterLock = await ProbeWriteLockAsync(isCounter: true, hotUuid);
        LockProbe appendLock = await ProbeWriteLockAsync(isCounter: false, hotUuid);

        report.AppendLine();
        report.AppendLine("### serialisation probe — a second writer for the same memory, lock_timeout 250 ms");
        report.AppendLine();
        report.AppendLine("| Placement | Second writer | SQL state |");
        report.AppendLine("|---|---|---|");
        report.AppendLine(LockRow(appendLock));
        report.AppendLine(LockRow(counterLock));

        TupleChurn appendChurn = await MeasureTupleChurnAsync(
            "B — append-only records", hotUuid, () => WriteAppendManyAsync(hotUuid));
        TupleChurn counterChurn = await MeasureTupleChurnAsync(
            "A — counter on the memory row", hotUuid, () => WriteCounterManyAsync(hotUuid));

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"### what {DirtyingIterations} recalls of one memory do to that memory's row");
        report.AppendLine();
        report.AppendLine("| Placement | ctid before | ctid after | row rewritten | n_dead_tup delta |");
        report.AppendLine("|---|---|---|---|---|");
        report.AppendLine(ChurnRow(appendChurn));
        report.AppendLine(ChurnRow(counterChurn));
        report.AppendLine();
        report.AppendLine(
            "`ctid` is the decisive column: it is the physical tuple address, so a change means the read "
            + "rewrote the row. `n_dead_tup` is indicative only — statistics are flushed per backend, so a "
            + "pooled connection from an earlier phase can land its dead tuples inside a later window.");

        Growth growth = await MeasureGrowthAsync();
        AppendGrowth(report, growth, broadRows.Count);

        await ResetFeedbackAsync();
        Actionability actionability = await MeasureActionabilityAsync(broadRows);
        AppendActionability(report, actionability);

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());

        // NFR-01 — a classification column that accepts free text is how content re-enters.
        shapeConstrained.ShouldBeTrue(
            "the retrieval-shape classification must be constrained by the schema, not by convention");

        // NFR-02 — the read path must return the same rows, field for field, and must still return them
        // when the feedback write inside the same unit of work throws.
        impact.RowsReturnedWhenFeedbackBroken.ShouldBe(broadRows.Count,
            "the comparison must be against a populated result, or identical-to-empty passes vacuously");
        impact.ResultsIdenticalWithFeedback.ShouldBeTrue(
            "feedback changed the retrieval's result set, its field values or its order");
        impact.FeedbackWriteActuallyFailed.ShouldBeTrue(
            "the injected feedback failure did not happen, so the boundary was not exercised");
        impact.InjectedSqlState.ShouldBe(PostgresErrorCodes.UndefinedTable,
            "the injected failure must be the missing feedback table, not an unrelated error");
        impact.ResultsIdenticalWhenFeedbackBroken.ShouldBeTrue(
            "a failing feedback write changed the retrieval's result set, its field values or its order");

        // NFR-02's stated discriminator. A second writer for the same memory must not queue behind the
        // first, and a counter on the memory row is exactly a queue.
        appendLock.SecondWriterBlocked.ShouldBeFalse(
            "appends must not contend; a blocked append invalidates the chosen placement");
        counterLock.SecondWriterBlocked.ShouldBeTrue(
            "the counter placement was expected to serialise on the shared row — if it no longer does, "
            + "re-measure before reusing this evidence");
        counterLock.SqlState.ShouldBe(PostgresErrorCodes.LockNotAvailable,
            "serialisation must be evidenced by a lock timeout specifically; any other SQL state means "
            + "the probe failed for an unrelated reason and is not evidence");

        // Reading must not rewrite the store's hottest table.
        appendChurn.RowRewritten.ShouldBeFalse("appends must leave the memory row physically untouched");
        counterChurn.RowRewritten.ShouldBeTrue(
            "the counter placement was expected to rewrite the memory row on every read");

        appended.P95Ms.ShouldBeLessThan(baseline.P95Ms + WriteOverheadBudgetMs,
            "the append write exceeded its per-retrieval budget; re-measure before raising this ceiling");

        // LADR-04 — feedback must not join the version chain or the append-only guarantee.
        triggers.AppendOnlyGuardTables.ShouldBe(["group_description", "memory_version"]);
        triggers.PrototypeFeedbackTriggers.ShouldBeEmpty(
            "a feedback table carrying a trigger has been pulled into a guarantee it must stay outside");

        // NFR-03 — all three tuning questions, answered from the mechanism rather than by inspection.
        actionability.NeverRecalledMatchesUntouchedSet.ShouldBeTrue(
            "the never-recalled list did not match the untouched set exactly");
        actionability.RecentlyCapturedExcluded.ShouldBe(RecentlyCapturedCount);
        actionability.RecentlyCapturedListedWithoutGrace.ShouldBe(RecentlyCapturedCount,
            "without the capture-age clause the recently captured must pollute the list — otherwise the "
            + "clause is not what excludes them and the criterion is untested");
        actionability.MissRetrievals.ShouldBe(MissCount);
        actionability.TotalRetrievals.ShouldBe(MissCount + 1,
            "retrievals must be countable, not inferred from record count — one hit retrieval returning "
            + $"{RecalledSubsetSize} memories is one retrieval, not {RecalledSubsetSize}");
        actionability.LastRecalledAt.ShouldNotBeNull(
            "the placement must answer recency, or A is not eliminated on the deciding question");
        actionability.ResetLeftNoRecords.ShouldBeTrue(
            "resetting the baseline must be possible without loss of knowledge");

        // Plans are recorded, not asserted. At fixture volume the planner's node choice is not the
        // property under test, and an assertion made under `enable_seqscan = off` would only restate the
        // setting. What the plans are evidence for is the relative cost of the capture-age clause, which
        // the shipped query surfaces have to answer for.
        actionability.NeverRecalledPlan.ShouldNotBeEmpty();
        actionability.MissRatePlan.ShouldNotBeEmpty();
    }

    private sealed record Measurement(
        string Name,
        double P50Ms,
        double P95Ms,
        double P95MinMs,
        double P95MaxMs,
        int Rounds)
    {
        public double P95SpreadMs => P95MaxMs - P95MinMs;
    }

    private sealed record Concurrency(string Name, double P50Ms, double P95Ms, double ElapsedSeconds, double OpsPerSecond);

    private sealed record LockProbe(string Name, bool SecondWriterBlocked, string SqlState);

    private sealed record TriggerAudit(
        IReadOnlyList<string> AppendOnlyGuardTables,
        IReadOnlyList<string> PrototypeFeedbackTriggers,
        IReadOnlyList<string> MemoryTableTriggers);

    private sealed record ReadPathImpact(
        bool ResultsIdenticalWithFeedback,
        bool ResultsIdenticalWhenFeedbackBroken,
        int RowsReturnedWhenFeedbackBroken,
        bool FeedbackWriteActuallyFailed,
        string InjectedSqlState,
        string InjectedError);

    private sealed record Growth(long Rows, long TotalBytes, double BytesPerRow);

    private sealed record TupleChurn(
        string Name,
        string CtidBefore,
        string CtidAfter,
        bool RowRewritten,
        long DeadTupleDelta);

    private sealed record Actionability(
        bool NeverRecalledMatchesUntouchedSet,
        long NeverRecalledCount,
        long ExpectedNeverRecalledCount,
        int RecentlyCapturedExcluded,
        int RecentlyCapturedListedWithoutGrace,
        long MissRetrievals,
        long TotalRetrievals,
        DateTimeOffset? LastRecalledAt,
        bool ResetLeftNoRecords,
        string NeverRecalledPlan,
        string MissRatePlan);

    private static string LatencyRow(Measurement m, double baselineP95Ms)
    {
        double delta = m.P95Ms - baselineP95Ms;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"| {m.Name} | {m.P50Ms:F3} | {m.P95Ms:F3} | {delta:F3} | "
            + $"{m.P95MinMs:F3}–{m.P95MaxMs:F3} | {m.P95SpreadMs:F3} |");
    }

    private static string ConcurrencyRow(Concurrency c) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"| {c.Name} | {c.P50Ms:F3} | {c.P95Ms:F3} | {c.ElapsedSeconds:F2} | {c.OpsPerSecond:F0} |");

    private static string ChurnRow(TupleChurn c) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"| {c.Name} | {c.CtidBefore} | {c.CtidAfter} | {(c.RowRewritten ? "yes" : "no")} | {c.DeadTupleDelta} |");

    private static string LockRow(LockProbe p) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"| {p.Name} | {(p.SecondWriterBlocked ? "blocked" : "proceeded")} | {p.SqlState} |");

    private static void AppendTriggerAudit(StringBuilder report, TriggerAudit audit)
    {
        report.AppendLine();
        report.AppendLine("### trigger audit — what the append-only guarantee actually covers");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- `append_only_guard` fires on: {Join(audit.AppendOnlyGuardTables)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- triggers on `memory`: {Join(audit.MemoryTableTriggers)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- triggers on the prototype feedback table: {Join(audit.PrototypeFeedbackTriggers)}");
    }

    private static void AppendReadPathImpact(StringBuilder report, ReadPathImpact impact)
    {
        report.AppendLine();
        report.AppendLine("### read-path isolation");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- every field of every row identical with feedback written: {impact.ResultsIdenticalWithFeedback}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- the injected feedback write really failed: {impact.FeedbackWriteActuallyFailed} ({impact.InjectedError})");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- the retrieval in that same unit of work still returned {impact.RowsReturnedWhenFeedbackBroken} rows, "
            + $"every field identical: {impact.ResultsIdenticalWhenFeedbackBroken}");
        report.AppendLine(
            "- this is the prototype boundary only: the shipped fire-and-forget writer and the retrieval "
            + "handler it hangs off are verified where they are built.");
    }

    private static void AppendGrowth(StringBuilder report, Growth growth, int rowsPerRetrieval)
    {
        double perRetrievalBytes = growth.BytesPerRow * rowsPerRetrieval;
        report.AppendLine();
        report.AppendLine("### growth projection — append-only placement");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- measured: {growth.Rows} rows occupy {growth.TotalBytes} bytes including indexes "
            + $"({growth.BytesPerRow:F0} bytes/row)");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- one retrieval returning {rowsPerRetrieval} memories writes ~{perRetrievalBytes / 1024:F1} KiB");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Retained under the stated bound — {RetentionDays} days or {RetentionRecordCap:N0} records, "
            + $"whichever is reached first. The unbounded columns show why the record cap is needed.");
        report.AppendLine();
        report.AppendLine("| Retrievals/day | records in 30 days | 30-day unbounded | 90-day unbounded | bounded by | retained under the bound |");
        report.AppendLine("|---|---|---|---|---|---|");
        foreach (int perDay in new[] { 200, 2_000, 20_000 })
        {
            double recordsIn30Days = (double)rowsPerRetrieval * perDay * RetentionDays;
            double bounded = Math.Min(recordsIn30Days, RetentionRecordCap) * growth.BytesPerRow;
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {perDay} | {recordsIn30Days:N0} | {perRetrievalBytes * perDay * RetentionDays / (1024 * 1024):F0} MiB "
                + $"| {perRetrievalBytes * perDay * 90 / (1024 * 1024):F0} MiB "
                + $"| {(recordsIn30Days > RetentionRecordCap ? "record cap" : "30 days")} "
                + $"| {bounded / (1024 * 1024):F0} MiB |");
        }
    }

    private static void AppendActionability(StringBuilder report, Actionability a)
    {
        report.AppendLine();
        report.AppendLine("### actionability — the three tuning questions");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- never recalled: {a.NeverRecalledCount} of {MemoryCount} (expected {a.ExpectedNeverRecalledCount}); "
            + $"matches the untouched set exactly: {a.NeverRecalledMatchesUntouchedSet}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- recently captured excluded by the {RecentlyCapturedGraceDays}-day capture-age clause: "
            + $"{a.RecentlyCapturedExcluded}; listed without it: {a.RecentlyCapturedListedWithoutGrace}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- retrievals in the {MissRateWindowDays}-day window: {a.TotalRetrievals}, of which "
            + $"{a.MissRetrievals} returned nothing "
            + $"({(a.TotalRetrievals == 0 ? 0 : (double)a.MissRetrievals / a.TotalRetrievals):P1} miss rate)");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- last recalled at: {a.LastRecalledAt?.ToString("O", CultureInfo.InvariantCulture) ?? "unanswerable"}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- reset left no records: {a.ResetLeftNoRecords}");
        report.AppendLine();
        report.AppendLine("#### plan — never recalled");
        report.AppendLine("```");
        report.AppendLine(a.NeverRecalledPlan);
        report.AppendLine("```");
        report.AppendLine("#### plan — miss rate over a window");
        report.AppendLine("```");
        report.AppendLine(a.MissRatePlan);
        report.AppendLine("```");
    }

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join(", ", values);

    private static double Percentile(double[] sortedAscending, double p)
    {
        double index = p * (sortedAscending.Length - 1);
        int lo = (int)Math.Floor(index);
        int hi = (int)Math.Ceiling(index);
        return lo == hi
            ? sortedAscending[lo]
            : (sortedAscending[lo] * (1 - (index - lo))) + (sortedAscending[hi] * (index - lo));
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        return Percentile(values, 0.50);
    }

    /// <summary>
    /// Measures every placement in <paramref name="placements"/> over <see cref="MeasurementRounds"/>
    /// rounds, rotating which placement goes first. The rotation and the per-round reset remove the two
    /// confounds a single fixed-order pass carries: the last placement measured otherwise runs against a
    /// larger feedback table and a <c>memory</c> table dirtied by the placement before it. The spread of
    /// a single placement's p95 across rounds is what says whether a gap between two placements is a
    /// property or scheduler noise, and nothing but repetition can supply it.
    /// </summary>
    private async Task<IReadOnlyList<Measurement>> MeasureLatencyAsync(
        (string Name, Func<Task> Run)[] placements)
    {
        Dictionary<string, List<(double P50Ms, double P95Ms)>> rounds =
            placements.ToDictionary(p => p.Name, _ => new List<(double, double)>(), StringComparer.Ordinal);

        for (int round = 0; round < MeasurementRounds; round++)
        {
            await ResetFeedbackAsync();
            await VacuumAsync("memory");
            await VacuumAsync("recall_feedback_probe");

            for (int offset = 0; offset < placements.Length; offset++)
            {
                (string name, Func<Task> run) = placements[(round + offset) % placements.Length];
                rounds[name].Add(await SampleAsync(run));
            }
        }

        return
        [
            .. placements.Select(placement =>
            {
                List<(double P50Ms, double P95Ms)> samples = rounds[placement.Name];
                return new Measurement(
                    placement.Name,
                    Median([.. samples.Select(s => s.P50Ms)]),
                    Median([.. samples.Select(s => s.P95Ms)]),
                    samples.Min(s => s.P95Ms),
                    samples.Max(s => s.P95Ms),
                    samples.Count);
            }),
        ];
    }

    private static async Task<(double P50Ms, double P95Ms)> SampleAsync(Func<Task> run)
    {
        for (int i = 0; i < WarmupIterations; i++)
        {
            await run();
        }

        var samples = new double[MeasuredIterations];
        var stopwatch = new Stopwatch();
        for (int i = 0; i < MeasuredIterations; i++)
        {
            stopwatch.Restart();
            await run();
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        return (Percentile(samples, 0.50), Percentile(samples, 0.95));
    }

    private SmoothAiProductContextMemoryDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
            .UseNpgsql(DataSource, npgsql => npgsql.UseSmoothAiProductContextMemoryHistory())
            .Options);

    /// <summary>
    /// Runs the same retrieval concurrently from independent connections, each writing feedback for the
    /// single memory every worker gets back. This is NFR-02's discriminating case: the workers differ
    /// only in where their feedback lands.
    /// </summary>
    private async Task<Concurrency> MeasureConcurrencyAsync(
        string name,
        MemorySearchCriteria criteria,
        Func<IReadOnlyList<CheapMemory>, Task> write)
    {
        var samples = new double[ConcurrentWorkers * OperationsPerWorker];
        Stopwatch wall = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, ConcurrentWorkers).Select(async worker =>
        {
            await using SmoothAiProductContextMemoryDbContext db = NewDbContext();
            var search = new NpgsqlMemorySearch(db);
            var stopwatch = new Stopwatch();
            for (int i = 0; i < OperationsPerWorker; i++)
            {
                stopwatch.Restart();
                IReadOnlyList<CheapMemory> rows = await search.SearchAsync(criteria, Ct);
                await write(rows);
                stopwatch.Stop();
                samples[(worker * OperationsPerWorker) + i] = stopwatch.Elapsed.TotalMilliseconds;
            }
        }));

        wall.Stop();
        Array.Sort(samples);
        return new Concurrency(
            name,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            wall.Elapsed.TotalSeconds,
            samples.Length / wall.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Holds an uncommitted feedback write for one memory, then attempts the same write from a second
    /// connection under a short <c>lock_timeout</c>. A placement that queues shows up here as
    /// <c>lock_not_available</c> rather than as a percentile that might be noise.
    /// </summary>
    private async Task<LockProbe> ProbeWriteLockAsync(bool isCounter, Guid hotUuid)
    {
        string name = isCounter ? "A — counter on the memory row" : "B — append-only records";
        string sql = isCounter
            ? "UPDATE memory SET recall_count = recall_count + 1 WHERE uuid = $1;"
            : """
              INSERT INTO recall_feedback_probe (retrieval_id, memory_uuid, shape, occurred_on)
              VALUES (gen_random_uuid(), $1, 'free_text', now());
              """;

        await using NpgsqlConnection holder = await DataSource.OpenConnectionAsync(Ct);
        await using NpgsqlTransaction holdOpen = await holder.BeginTransactionAsync(Ct);
        await using (var first = new NpgsqlCommand(sql, holder, holdOpen))
        {
            first.Parameters.AddWithValue(hotUuid);
            await first.ExecuteNonQueryAsync(Ct);
        }

        await using NpgsqlConnection contender = await DataSource.OpenConnectionAsync(Ct);
        await using (var timeout = new NpgsqlCommand("SET lock_timeout = '250ms';", contender))
        {
            await timeout.ExecuteNonQueryAsync(Ct);
        }

        try
        {
            await using var second = new NpgsqlCommand(sql, contender);
            second.Parameters.AddWithValue(hotUuid);
            await second.ExecuteNonQueryAsync(Ct);
            return new LockProbe(name, SecondWriterBlocked: false, SqlState: "none");
        }
        catch (PostgresException ex)
        {
            // Only a lock timeout is evidence of serialisation. Any other SQL state means the probe
            // itself broke, and reading that as contention would manufacture the result.
            return new LockProbe(
                name,
                string.Equals(ex.SqlState, PostgresErrorCodes.LockNotAvailable, StringComparison.Ordinal),
                ex.SqlState);
        }
        finally
        {
            await holdOpen.RollbackAsync(Ct);
        }
    }

    /// <summary>
    /// Compares what the retrieval returns — every field of every row, in order — with feedback written
    /// and with the feedback write throwing. The failing write sits <em>inside</em> the same unit of work
    /// as the retrieval, after the rows are in hand, which is the only arrangement where the exception
    /// could reach the caller: attempting the two independently would prove nothing, because nothing
    /// could propagate between them.
    /// <para>
    /// This is the prototype boundary. The shipped fire-and-forget writer, and the retrieval handler it
    /// hangs off, are verified against the write path when that is built.
    /// </para>
    /// </summary>
    private async Task<ReadPathImpact> MeasureReadPathImpactAsync(
        MemorySearchCriteria criteria,
        IReadOnlyList<CheapMemory> expected)
    {
        string[] expectedRows = [.. expected.Select(Fingerprint)];

        IReadOnlyList<CheapMemory> withFeedback = await Search.SearchAsync(criteria, Ct);
        await WriteAppendAsync(withFeedback, RetrievalShapes[0]);

        string injectedSqlState = "none";
        string injectedError = "the injected write succeeded";

        // The unit a caller would invoke: retrieve, then attempt the feedback write and absorb its
        // failure, then hand the rows back. If the boundary were missing the exception would escape here
        // and the retrieval would be lost with it, which is the failure NFR-02 forbids.
        async Task<IReadOnlyList<CheapMemory>> RetrieveWithFailingFeedbackAsync()
        {
            IReadOnlyList<CheapMemory> rows = await Search.SearchAsync(criteria, Ct);
            try
            {
                await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
                await using var broken = new NpgsqlCommand(
                    "INSERT INTO recall_feedback_absent (memory_uuid) VALUES (gen_random_uuid());", conn);
                await broken.ExecuteNonQueryAsync(Ct);
            }
            catch (PostgresException ex)
            {
                injectedSqlState = ex.SqlState;
                injectedError = $"{ex.SqlState} {ex.MessageText}";
            }

            return rows;
        }

        IReadOnlyList<CheapMemory> withBrokenFeedback = await RetrieveWithFailingFeedbackAsync();

        return new ReadPathImpact(
            expectedRows.SequenceEqual(withFeedback.Select(Fingerprint), StringComparer.Ordinal),
            expectedRows.SequenceEqual(withBrokenFeedback.Select(Fingerprint), StringComparer.Ordinal),
            withBrokenFeedback.Count,
            !string.Equals(injectedSqlState, "none", StringComparison.Ordinal),
            injectedSqlState,
            injectedError);
    }

    /// <summary>
    /// Every field of a returned row, flattened. Record equality would compare the collection members by
    /// reference and report two identical result sets as different, so the comparison NFR-02 asks for has
    /// to be made on values.
    /// </summary>
    private static string Fingerprint(CheapMemory row) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{row.Uuid}|{row.GroupUuid}|{row.Name}|{row.Description}|{row.Statement}|{row.ContentSummary}|"
            + $"{row.Kind}|{string.Join(",", row.Facets)}|{string.Join(",", row.Tags)}|{row.Status}|"
            + $"{row.Confidence}|{row.ScopeDimension}|{row.ScopeIdentifier}|{row.ValidFrom:O}|"
            + $"{row.ValidUntil:O}|{row.Version}|{row.IsCurrent}|"
            + $"{string.Join(",", row.Sources.Select(s => s.ToString()))}|{row.CreatedOn:O}");

    /// <summary>
    /// Records whether repeatedly recalling one memory rewrites that memory's row. <c>ctid</c> is the
    /// physical tuple address, so a change is proof the read produced a new row version — the property
    /// that makes a popular memory a hot row, measured without depending on statistics collection.
    /// </summary>
    private async Task<TupleChurn> MeasureTupleChurnAsync(string name, Guid hotUuid, Func<Task> work)
    {
        await VacuumAsync("memory");
        string before = await CtidAsync(hotUuid);
        long deadBefore = await DeadTuplesAsync("memory");

        await work();

        string after = await CtidAsync(hotUuid);
        long deadAfter = await DeadTuplesAsync("memory");
        return new TupleChurn(
            name,
            before,
            after,
            !string.Equals(before, after, StringComparison.Ordinal),
            Math.Max(deadAfter - deadBefore, 0));
    }

    private async Task<string> CtidAsync(Guid uuid)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT ctid::text FROM memory WHERE uuid = $1;", conn);
        command.Parameters.AddWithValue(uuid);
        return (string?)await command.ExecuteScalarAsync(Ct) ?? "missing";
    }

    private async Task VacuumAsync(string table)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand($"VACUUM {table};", conn);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> DeadTuplesAsync(string table)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using (var flush = new NpgsqlCommand("SELECT pg_stat_force_next_flush();", conn))
        {
            await flush.ExecuteNonQueryAsync(Ct);
        }

        await using var read = new NpgsqlCommand(
            "SELECT coalesce(n_dead_tup, 0) FROM pg_stat_user_tables WHERE relname = $1;", conn);
        read.Parameters.AddWithValue(table);
        return (long?)await read.ExecuteScalarAsync(Ct) ?? 0L;
    }

    private async Task WriteCounterAsync(IReadOnlyList<CheapMemory> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "UPDATE memory SET recall_count = recall_count + 1 WHERE uuid = ANY($1);", conn);
        command.Parameters.AddWithValue(rows.Select(r => r.Uuid).ToArray());
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>
    /// One retrieval writes one row per returned memory, all sharing a generated retrieval identifier.
    /// Without that identifier a retrieval returning fifty memories is indistinguishable from fifty
    /// retrievals, and the miss-rate question becomes unanswerable.
    /// </summary>
    private async Task WriteAppendAsync(IReadOnlyList<CheapMemory> rows, string shape)
    {
        if (rows.Count == 0)
        {
            await WriteMissAsync(shape);
            return;
        }

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO recall_feedback_probe (retrieval_id, memory_uuid, shape, occurred_on)
            SELECT $3, u, $2, now() FROM unnest($1::uuid[]) AS u;
            """,
            conn);
        command.Parameters.AddWithValue(rows.Select(r => r.Uuid).ToArray());
        command.Parameters.AddWithValue(shape);
        command.Parameters.AddWithValue(Guid.NewGuid());
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>
    /// The miss record: a retrieval identifier, a shape and a time, and no memory identity, because a
    /// retrieval that returned nothing has none to record. A counter on the memory row has nowhere to
    /// put this at all.
    /// </summary>
    private async Task WriteMissAsync(string shape)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO recall_feedback_probe (retrieval_id, memory_uuid, shape, occurred_on)
            VALUES (gen_random_uuid(), NULL, $1, now());
            """,
            conn);
        command.Parameters.AddWithValue(shape);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task WriteCounterManyAsync(Guid hotUuid)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "UPDATE memory SET recall_count = recall_count + 1 WHERE uuid = $1;", conn);
        command.Parameters.AddWithValue(hotUuid);
        for (int i = 0; i < DirtyingIterations; i++)
        {
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    private async Task WriteAppendManyAsync(Guid hotUuid)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO recall_feedback_probe (retrieval_id, memory_uuid, shape, occurred_on)
            VALUES (gen_random_uuid(), $1, 'free_text', now());
            """,
            conn);
        command.Parameters.AddWithValue(hotUuid);
        for (int i = 0; i < DirtyingIterations; i++)
        {
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    /// <summary>
    /// Only a check violation counts as rejection. Catching every <see cref="PostgresException"/> would
    /// let a missing table or a typo in the prototype read as a working constraint.
    /// </summary>
    private async Task<bool> ShapeColumnRejectsFreeTextAsync()
    {
        try
        {
            await WriteMissAsync("what did we decide about the acquisition pricing");
            return false;
        }
        catch (PostgresException ex)
            when (string.Equals(ex.SqlState, PostgresErrorCodes.CheckViolation, StringComparison.Ordinal))
        {
            return true;
        }
    }

    private async Task<Growth> MeasureGrowthAsync()
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using (var analyze = new NpgsqlCommand("ANALYZE recall_feedback_probe;", conn))
        {
            await analyze.ExecuteNonQueryAsync(Ct);
        }

        await using var read = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM recall_feedback_probe),
                   pg_total_relation_size('recall_feedback_probe');
            """,
            conn);
        await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        long rows = reader.GetInt64(0);
        long bytes = reader.GetInt64(1);
        return new Growth(rows, bytes, rows == 0 ? 0 : (double)bytes / rows);
    }

    /// <summary>
    /// Truncating feedback and zeroing the counter — the resettable baseline LADR-04 calls legitimate
    /// rather than destructive, and a precondition for the before/after comparison NFR-03 requires.
    /// </summary>
    private async Task ResetFeedbackAsync()
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            TRUNCATE recall_feedback_probe;
            UPDATE memory SET recall_count = 0 WHERE recall_count <> 0;
            """,
            conn);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<Actionability> MeasureActionabilityAsync(IReadOnlyList<CheapMemory> candidates)
    {
        // Recalled memories are taken from the long-lived cohort only: a recalled memory that is also
        // recently captured would be excluded twice and the arithmetic would stop proving anything.
        Guid[] recalled =
        [
            .. candidates
                .Where(r => !r.Name.StartsWith("recent-", StringComparison.Ordinal))
                .Take(RecalledSubsetSize)
                .Select(r => r.Uuid),
        ];
        recalled.Length.ShouldBe(RecalledSubsetSize);

        await using (NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct))
        {
            await using var seedHits = new NpgsqlCommand(
                """
                INSERT INTO recall_feedback_probe (retrieval_id, memory_uuid, shape, occurred_on)
                SELECT $2, u, 'free_text', now() FROM unnest($1::uuid[]) AS u;
                """,
                conn);
            seedHits.Parameters.AddWithValue(recalled);
            seedHits.Parameters.AddWithValue(Guid.NewGuid());
            await seedHits.ExecuteNonQueryAsync(Ct);
        }

        for (int i = 0; i < MissCount; i++)
        {
            await WriteMissAsync(RetrievalShapes[0]);
        }

        long neverRecalled = await ScalarAsync(NeverRecalledSql, null);
        long recalledStillListed = await ScalarAsync(RecalledStillListedSql, cmd => cmd.Parameters.AddWithValue(recalled));
        long recentlySurviving = await ScalarAsync(RecentlyCapturedSql(applyGrace: true), null);
        long recentlyListedWithoutGrace = await ScalarAsync(RecentlyCapturedSql(applyGrace: false), null);

        (long missRetrievals, long totalRetrievals) = await MissRateAsync();
        DateTimeOffset? lastRecalled = await LastRecalledAsync(recalled[0]);

        string neverRecalledPlan = await ExplainAsync(NeverRecalledSql);
        string missRatePlan = await ExplainAsync(MissRateSql);

        await ResetFeedbackAsync();
        bool resetClean = await ScalarAsync("SELECT count(*) FROM recall_feedback_probe;", null) == 0;

        long expectedNeverRecalled = MemoryCount - RecentlyCapturedCount - RecalledSubsetSize;
        return new Actionability(
            recalledStillListed == 0 && neverRecalled == expectedNeverRecalled,
            neverRecalled,
            expectedNeverRecalled,
            (int)(RecentlyCapturedCount - recentlySurviving),
            (int)recentlyListedWithoutGrace,
            missRetrievals,
            totalRetrievals,
            lastRecalled,
            resetClean,
            neverRecalledPlan,
            missRatePlan);
    }

    /// <summary>
    /// Never recalled, excluding the recently captured. The capture-age clause has to reach
    /// <c>memory_version.created_on</c>: the stable memory row carries no timestamp, so "recently
    /// captured" is not a column on the thing being measured.
    /// </summary>
    private static string NeverRecalledSql =>
        $"""
        SELECT count(*)
        FROM memory m
        WHERE NOT EXISTS (
                  SELECT 1 FROM recall_feedback_probe f WHERE f.memory_uuid = m.uuid)
          AND (SELECT min(v.created_on) FROM memory_version v WHERE v.memory_id = m.id)
              < now() - interval '{RecentlyCapturedGraceDays} days';
        """;

    private static string RecalledStillListedSql =>
        """
        SELECT count(*)
        FROM memory m
        WHERE m.uuid = ANY($1)
          AND NOT EXISTS (SELECT 1 FROM recall_feedback_probe f WHERE f.memory_uuid = m.uuid);
        """;

    private static string MissRateSql =>
        $"""
        SELECT count(DISTINCT retrieval_id) FILTER (WHERE memory_uuid IS NULL) AS misses,
               count(DISTINCT retrieval_id) AS retrievals
        FROM recall_feedback_probe
        WHERE occurred_on >= now() - interval '{MissRateWindowDays} days';
        """;

    private static string RecentlyCapturedSql(bool applyGrace)
    {
        string grace = applyGrace
            ? $"AND (SELECT min(v.created_on) FROM memory_version v WHERE v.memory_id = m.id) "
              + $"< now() - interval '{RecentlyCapturedGraceDays} days'"
            : string.Empty;

        return $"""
            SELECT count(*)
            FROM memory m
            WHERE m.name LIKE 'recent-%'
              AND NOT EXISTS (SELECT 1 FROM recall_feedback_probe f WHERE f.memory_uuid = m.uuid)
              {grace};
            """;
    }

    private async Task<(long Misses, long Retrievals)> MissRateAsync()
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(MissRateSql, conn);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<long> ScalarAsync(string sql, Action<NpgsqlCommand>? addParameters)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, conn);
        addParameters?.Invoke(command);
        return (long?)await command.ExecuteScalarAsync(Ct) ?? 0L;
    }

    private async Task<DateTimeOffset?> LastRecalledAsync(Guid uuid)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT max(occurred_on) FROM recall_feedback_probe WHERE memory_uuid = $1;", conn);
        command.Parameters.AddWithValue(uuid);
        object? value = await command.ExecuteScalarAsync(Ct);
        return value is DateTime instant ? new DateTimeOffset(instant) : null;
    }

    /// <summary>
    /// Plans are captured with <c>SET LOCAL enable_seqscan = off</c> so a predicate the index cannot
    /// serve stays visible at fixture volume, matching how the recall-tuning harness reads plans. They are
    /// recorded as evidence of relative cost, not asserted on: under that setting an assertion about scan
    /// nodes would only restate the setting, and the planner's node choice at fixture volume is not the
    /// property this harness exists to settle.
    /// </summary>
    private async Task<string> ExplainAsync(string sql)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(Ct);
        await using (var off = new NpgsqlCommand("SET LOCAL enable_seqscan = off;", conn, tx))
        {
            await off.ExecuteNonQueryAsync(Ct);
        }

        var lines = new List<string>();
        await using (var explain = new NpgsqlCommand($"EXPLAIN (FORMAT TEXT) {sql.TrimEnd(';', '\n', '\r', ' ')}", conn, tx))
        await using (NpgsqlDataReader reader = await explain.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                lines.Add(reader.GetString(0));
            }
        }

        await tx.RollbackAsync(Ct);
        return string.Join(Environment.NewLine, lines);
    }

    private async Task<TriggerAudit> AuditTriggersAsync()
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, t.tgname, p.proname
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_proc p ON p.oid = t.tgfoid
            WHERE NOT t.tgisinternal
              AND c.relnamespace = 'public'::regnamespace
            ORDER BY c.relname, t.tgname;
            """,
            conn);

        var guarded = new SortedSet<string>(StringComparer.Ordinal);
        var onMemory = new List<string>();
        var onFeedback = new List<string>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            string table = reader.GetString(0);
            string trigger = reader.GetString(1);
            string function = reader.GetString(2);

            if (string.Equals(function, "append_only_guard", StringComparison.Ordinal))
            {
                guarded.Add(table);
            }

            if (string.Equals(table, "memory", StringComparison.Ordinal))
            {
                onMemory.Add($"{trigger} -> {function}()");
            }

            if (string.Equals(table, "recall_feedback_probe", StringComparison.Ordinal))
            {
                onFeedback.Add($"{trigger} -> {function}()");
            }
        }

        return new TriggerAudit([.. guarded], onFeedback, onMemory);
    }

    /// <summary>
    /// Both prototypes, created only in this test's database. A is a column on the hottest table; B is a
    /// separate table with the identity, recency and retrieval indexes its queries need, and a
    /// <c>CHECK</c> standing in for the bounded classification set.
    /// </summary>
    private async Task CreatePrototypeObjectsAsync()
    {
        string shapes = string.Join(", ", RetrievalShapes.Select(s => $"'{s}'"));

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"""
            ALTER TABLE memory ADD COLUMN recall_count bigint NOT NULL DEFAULT 0;
            CREATE INDEX ix_memory_recall_count ON memory (recall_count) WHERE recall_count = 0;

            CREATE TABLE recall_feedback_probe (
                id bigserial PRIMARY KEY,
                retrieval_id uuid NOT NULL,
                memory_uuid uuid NULL,
                shape text NOT NULL,
                occurred_on timestamptz NOT NULL,
                CONSTRAINT ck_recall_feedback_probe_shape CHECK (shape IN ({shapes}))
            );
            CREATE INDEX ix_recall_feedback_probe_memory_uuid ON recall_feedback_probe (memory_uuid);
            CREATE INDEX ix_recall_feedback_probe_occurred_on ON recall_feedback_probe (occurred_on DESC);
            CREATE INDEX ix_recall_feedback_probe_retrieval ON recall_feedback_probe (occurred_on, retrieval_id);
            """,
            conn);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>
    /// Seeded server-side: the shapes measured are the production row shapes, and the write path would
    /// contribute nothing but round trips. Three cohorts — a bulk captured 30 days ago, a recent cohort
    /// so the capture-age clause has something to exclude, and one deliberately singular memory every
    /// concurrent worker retrieves.
    /// </summary>
    private async Task SeedAsync()
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO memory_group (uuid, scope_dimension, initiative_id, tickets, created_on)
            VALUES (gen_random_uuid(), 'product', 1, '[]'::jsonb, now());

            INSERT INTO memory (uuid, lineage_id, group_id, name, description, subject_slug, tags, facets)
            SELECT gen_random_uuid(), gen_random_uuid(),
                   (SELECT max(id) FROM memory_group),
                   CASE WHEN g <= {RecentlyCapturedCount} THEN 'recent-bench-' || g ELSE 'bench-' || g END,
                   'bench subject ' || g, 'bench-subject-' || g, ARRAY[]::text[], ARRAY[]::text[]
            FROM generate_series(1, {MemoryCount - 1}) AS g;

            INSERT INTO memory (uuid, lineage_id, group_id, name, description, subject_slug, tags, facets)
            VALUES (gen_random_uuid(), gen_random_uuid(),
                    (SELECT max(id) FROM memory_group),
                    'singular popular anchor', 'the singular anchor subject', 'singular-anchor',
                    ARRAY[]::text[], ARRAY[]::text[]);

            INSERT INTO memory_version
                (memory_id, version, is_current, statement, content_summary, blob_address, kind,
                 confidence, status, sources, valid_from, created_on)
            SELECT m.id, 1, true, 'bench claim ' || m.id, 'bench summary ' || m.id, NULL,
                   'reference', 80, 'approved', '[]'::jsonb,
                   now() - interval '30 days',
                   CASE WHEN m.name LIKE 'recent-%' THEN now() ELSE now() - interval '30 days' END
            FROM memory m;

            ANALYZE memory;
            ANALYZE memory_version;
            ANALYZE memory_group;
            """,
            conn);
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
