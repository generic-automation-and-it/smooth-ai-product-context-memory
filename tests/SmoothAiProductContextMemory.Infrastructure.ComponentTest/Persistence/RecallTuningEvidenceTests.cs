using System.Diagnostics;
using System.Globalization;
using System.Text;
using Npgsql;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// Evidence harness for the HLD-001 deferred candidate-recall improvements: measures precision and
/// recall of the current <c>simple</c> full-text configuration against a stemmed <c>english</c>
/// configuration and <c>pg_trgm</c> word similarity, over one committed synthetic fixture containing
/// inflection pairs, surface-form variants and related-but-distinct negative controls.
/// </summary>
/// <remarks>
/// Env-gated because it is evidence recorded to the HLD folder, not a pass/fail the PR gate needs.
/// Run with <c>SMOOTH_FTS_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest
/// --filter RecallTuningEvidenceTests</c>.
/// <para>
/// Alternative index/extension objects are created only inside this test's isolated database; the
/// production schema is untouched. Plans are captured with <c>SET LOCAL enable_seqscan = off</c> so a
/// predicate the index cannot serve is visible even over a tiny fixture. Wall clock alone proves
/// nothing at this volume — the access path and the precision/recall table are the result.
/// </para>
/// </remarks>
public sealed class RecallTuningEvidenceTests : PersistenceTestBase
{
    private const int MeasuredIterations = 100;

    public RecallTuningEvidenceTests(AspireFixture aspire) : base(aspire) { }

    /// <summary>
    /// One synthetic memory: subject name/description on the stable row, claim statement/summary on
    /// the version row — matching where the two FTS indexes actually look.
    /// </summary>
    private sealed record FixtureMemory(string Key, string Name, string Description, string Statement, string Summary);

    /// <summary>A free-text query and the fixture keys that are genuinely relevant to it.</summary>
    private sealed record QueryCase(string Category, string Query, string[] Relevant);

    /// <summary>
    /// The committed baseline fixture. Content is synthetic. Categories:
    /// inflection pairs (stemming should help), surface-form variants (trigram should help), and
    /// related-but-distinct negative controls (nothing should start matching these).
    /// </summary>
    private static readonly FixtureMemory[] Corpus =
    [
        // Inflection / stemming pairs — stored form differs from the query's inflection.
        new("migrations", "Database migrations policy", "How schema migrations are reviewed",
            "All schema migrations require a reversible down step", "Reversibility rule for migrations"),
        new("caching", "Caching strategy", "Response caching for hot reads",
            "Hot reads are cached with a five minute expiry", "Cache expiry decision"),
        new("indexes", "Index maintenance", "Rebuilding bloated indexes",
            "Bloated indexes are rebuilt during the weekly window", "Weekly index rebuild"),
        new("retries", "Retry policy", "Retrying failed webhook deliveries",
            "Failed deliveries are retried three times with backoff", "Webhook retry decision"),
        // Surface-form / spelling variants — trigram territory.
        new("postgres-replication", "Postgres replication", "Streaming replication topology",
            "The replica lags behind the primary by design", "Replication topology"),
        new("kubernetes-quota", "Kubernetes resource quota", "Namespace quota defaults",
            "Every namespace receives a default cpu and memory quota", "Namespace quota defaults"),
        // Negative controls — related-but-distinct; matching these is a precision failure.
        new("sections", "Section heading style", "Formatting of section headings in documents",
            "Section headings use sentence case", "Heading style rule"),
        new("authors", "Book authors registry", "Tracking cited authors and citations",
            "Every cited author appears once in the registry", "Author registry rule"),
        new("authentication", "Authentication flow", "Login token issuance",
            "Login issues a short lived token", "Token issuance decision"),
        new("cachet", "Cachet display", "Displaying quality cachet badges",
            "Quality badges render next to the title", "Badge rendering rule"),
        // Filler so result sets are not trivially the whole table.
        new("billing", "Billing cycle", "Monthly invoice generation",
            "Invoices generate on the first of the month", "Invoice timing"),
        new("telemetry", "Telemetry pipeline", "Span export batching",
            "Spans export in batches of five hundred", "Span batching decision"),
        new("onboarding", "Onboarding checklist", "Steps for new repositories",
            "New repositories start from the shared template", "Template rule"),
        new("locales", "Locale fallback", "Falling back to the default locale",
            "Missing translations fall back to the default locale", "Locale fallback rule"),
    ];

    private static readonly QueryCase[] Cases =
    [
        // Inflection: query form differs from stored form.
        new("stemming", "migration", ["migrations"]),
        new("stemming", "cached responses", ["caching"]),
        new("stemming", "index rebuild", ["indexes"]),
        new("stemming", "retried deliveries", ["retries"]),
        // Surface variants: misspelling / morphology trigram might catch.
        new("trigram", "postgress replication", ["postgres-replication"]),
        new("trigram", "kubernets quota", ["kubernetes-quota"]),
        // Negative controls: the query has one true subject; near-neighbours must stay out.
        new("negative", "session handling", []),
        new("negative", "authorization rules", []),
        new("negative", "cache invalidation", ["caching"]),
        // Exact behaviour that already works must keep working.
        new("baseline", "webhook", ["retries"]),
        new("baseline", "invoice generation", ["billing"]),
    ];

    [Fact]
    public async Task Recall_precision_and_plans_for_simple_english_and_trigram()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable("SMOOTH_FTS_BENCH"), "1", StringComparison.Ordinal),
            "Set SMOOTH_FTS_BENCH=1 to record the recall-tuning measurements.");

        await SeedAsync();
        await CreateAlternativeObjectsAsync();

        var report = new StringBuilder();
        report.AppendLine("| Case | Query | Config | Retrieved | Relevant | Hits | Precision | Recall |");
        report.AppendLine("|---|---|---|---|---|---|---|---|");

        var totals = new Dictionary<string, (int Hits, int Retrieved, int Relevant)>();
        foreach (QueryCase queryCase in Cases)
        {
            foreach ((string config, string predicate) in Predicates())
            {
                IReadOnlyList<string> retrieved = await RunAsync(predicate, queryCase.Query);
                int hits = retrieved.Count(queryCase.Relevant.Contains);
                (int h, int ret, int rel) = totals.GetValueOrDefault(config);
                totals[config] = (h + hits, ret + retrieved.Count, rel + queryCase.Relevant.Length);
                report.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {queryCase.Category} | {queryCase.Query} | {config} | {retrieved.Count} | {queryCase.Relevant.Length} | {hits} "
                    + $"| {Ratio(hits, retrieved.Count)} | {Ratio(hits, queryCase.Relevant.Length)} |"));
            }
        }

        report.AppendLine();
        report.AppendLine("| Config | Micro precision | Micro recall | p50 (ms) | p95 (ms) |");
        report.AppendLine("|---|---|---|---|---|");
        foreach ((string config, string predicate) in Predicates())
        {
            (int hits, int retrieved, int relevant) = totals[config];
            (double p50, double p95) = await MeasureAsync(predicate);
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {config} | {Ratio(hits, retrieved)} | {Ratio(hits, relevant)} | {p50:F3} | {p95:F3} |"));
        }

        report.AppendLine();
        foreach ((string config, string predicate) in Predicates())
        {
            report.AppendLine($"### plan — {config}");
            report.AppendLine("```");
            report.AppendLine(await ExplainAsync(predicate));
            report.AppendLine("```");
        }

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
    }

    private static string Ratio(int numerator, int denominator)
        => denominator == 0 ? "—" : (numerator / (double)denominator).ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// The three candidate predicates over the same shape the production query emits: subject-row OR
    /// version-row match, joined current-only. <c>$1</c> is the query text.
    /// </summary>
    private static IEnumerable<(string Config, string Predicate)> Predicates()
    {
        yield return ("simple",
            "to_tsvector('simple', m.name || ' ' || m.description) @@ plainto_tsquery('simple', $1)"
            + " OR to_tsvector('simple', v.statement || ' ' || v.content_summary) @@ plainto_tsquery('simple', $1)");
        yield return ("english",
            "to_tsvector('english', m.name || ' ' || m.description) @@ plainto_tsquery('english', $1)"
            + " OR to_tsvector('english', v.statement || ' ' || v.content_summary) @@ plainto_tsquery('english', $1)");
        yield return ("pg_trgm",
            "(m.name || ' ' || m.description) %> $1"
            + " OR (v.statement || ' ' || v.content_summary) %> $1");
    }

    private async Task<IReadOnlyList<string>> RunAsync(string predicate, string query)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT m.subject_slug FROM memory m JOIN memory_version v ON v.memory_id = m.id AND v.is_current WHERE {predicate}",
            conn);
        cmd.Parameters.AddWithValue(query);
        var rows = new List<string>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<(double P50, double P95)> MeasureAsync(string predicate)
    {
        var samples = new double[MeasuredIterations];
        var stopwatch = new Stopwatch();
        for (int i = 0; i < MeasuredIterations; i++)
        {
            stopwatch.Restart();
            await RunAsync(predicate, "migration");
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        return (Percentile(samples, 0.50), Percentile(samples, 0.95));
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

    private async Task<string> ExplainAsync(string predicate)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(Ct);
        await using (var setCmd = new NpgsqlCommand("SET LOCAL enable_seqscan = off", conn, tx))
        {
            await setCmd.ExecuteNonQueryAsync(Ct);
        }

        await using var cmd = new NpgsqlCommand(
            $"EXPLAIN (FORMAT TEXT) SELECT m.subject_slug FROM memory m JOIN memory_version v ON v.memory_id = m.id AND v.is_current WHERE {predicate}",
            conn,
            tx);
        cmd.Parameters.AddWithValue("migration");
        var lines = new StringBuilder();
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                lines.AppendLine(reader.GetString(0));
            }
        }

        await tx.RollbackAsync(Ct);
        return lines.ToString();
    }

    private async Task SeedAsync()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        foreach (FixtureMemory row in Corpus)
        {
            var memory = TestEntities.NewMemory(group.Id, row.Name, row.Description, subjectSlug: row.Key);
            Db.Memories.Add(memory);
            await Db.SaveChangesAsync(Ct);
            var version = TestEntities.NewVersion(memory.Id, 1, row.Statement);
            version.ContentSummary = row.Summary;
            Db.MemoryVersions.Add(version);
        }

        await Db.SaveChangesAsync(Ct);
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var analyze = new NpgsqlCommand("ANALYZE memory; ANALYZE memory_version;", conn);
        await analyze.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Candidate objects, test-database-local only — the production schema gains nothing here.</summary>
    private async Task CreateAlternativeObjectsAsync()
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            """
            CREATE INDEX bench_memory_fts_en ON memory
                USING gin (to_tsvector('english', name || ' ' || description));
            CREATE INDEX bench_version_fts_en ON memory_version
                USING gin (to_tsvector('english', statement || ' ' || content_summary));
            CREATE EXTENSION IF NOT EXISTS pg_trgm;
            CREATE INDEX bench_memory_trgm ON memory
                USING gin ((name || ' ' || description) gin_trgm_ops);
            CREATE INDEX bench_version_trgm ON memory_version
                USING gin ((statement || ' ' || content_summary) gin_trgm_ops);
            """,
            conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}
