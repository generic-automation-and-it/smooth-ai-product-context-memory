using System.Diagnostics;
using System.Globalization;
using System.Text;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// NFR-02 evidence: the three query shapes at target volume, with plans, plus the one-hop comparison
/// against the pre-cutover relational baseline.
/// </summary>
/// <remarks>
/// Env-gated because seeding 3,000 memories and 10,000 edges costs minutes, and the output is evidence
/// committed to the HLD folder rather than a pass/fail the PR gate needs on every push. Run with
/// <c>SMOOTH_AGE_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest
/// --filter Nfr02BenchmarkTests</c>.
/// <para>
/// Wall-clock alone would not settle NFR-02: at this volume almost anything is fast, so each shape's
/// plan is captured and asserted to be an index or graph traversal. A passing p95 over a sequential
/// scan is a false pass.
/// </para>
/// </remarks>
public sealed class Nfr02BenchmarkTests : PersistenceTestBase
{
    private const int MemoryCount = 3_000;
    private const int RelationshipCount = 10_000;
    private const int WarmupIterations = 20;
    private const int MeasuredIterations = 100;

    /// <summary>Pre-cutover relational one-hop p95, from nfrs/NFR-02-one-hop-baseline.md.</summary>
    private const double RelationalBaselineP95Ms = 0.429;

    private const double DepthThreeTargetMs = 50.0;
    private const double OneHopTargetMs = 10.0;
    private const double ComposedTargetMs = 100.0;

    public Nfr02BenchmarkTests(AspireFixture aspire) : base(aspire) { }

    private IMemoryTraversal Traversal => new NpgsqlMemoryTraversal(Db);

    private NpgsqlMemoryGraph Graph => new(Db);

    [Fact]
    public async Task Nfr02_ThreeShapesAtTargetVolume_WithPlans()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable("SMOOTH_AGE_BENCH"), "1", StringComparison.Ordinal),
            "Set SMOOTH_AGE_BENCH=1 to record the NFR-02 traversal measurements.");

        var report = new StringBuilder();
        Stopwatch seedTimer = Stopwatch.StartNew();
        Guid[] uuids = await SeedAsync();
        seedTimer.Stop();
        report.AppendLine(CultureInfo.InvariantCulture, $"seed: {MemoryCount} memories, {RelationshipCount} edges in {seedTimer.Elapsed.TotalSeconds:F1}s");
        report.AppendLine(await RelationDistributionAsync());

        (Guid source, Guid target) = await FindDepthThreePairAsync(uuids);
        report.AppendLine(CultureInfo.InvariantCulture, $"depth-3 endpoints: {source} -> {target}");

        var depthThree = new MemoryPathQuery
        {
            SourceUuid = source,
            TargetUuid = target,
            MaxDepth = 3,
            Relation = MemoryRelation.RelatesTo,
        };
        var composed = new MemoryPathQuery
        {
            SourceUuid = source,
            MaxDepth = 3,
            Relation = MemoryRelation.RelatesTo,
            Kind = Domain.Entities.MemoryVersion.KindValue.Decision,
        };

        Measurement depthThreeResult = await MeasureAsync(
            "depth-3 bounded path, filtered by relation type",
            () => Traversal.FindPathsAsync(depthThree, Ct));
        Measurement oneHopResult = await MeasureAsync(
            "one-hop reverse lookup",
            () => Graph.ListTouchingAsync(target, Ct));
        Measurement composedResult = await MeasureAsync(
            "composed traversal plus relational filter",
            () => Traversal.FindPathsAsync(composed, Ct));

        string depthThreePlan = await ExplainTraversalAsync(depthThree);
        string oneHopPlan = await ExplainOneHopAsync(target);
        string composedPlan = await ExplainTraversalAsync(composed);

        report.AppendLine();
        report.AppendLine("| Shape | p50 (ms) | p95 (ms) | Target (ms) | Verdict |");
        report.AppendLine("|---|---|---|---|---|");
        report.AppendLine(Row(depthThreeResult, DepthThreeTargetMs));
        report.AppendLine(Row(oneHopResult, OneHopTargetMs));
        report.AppendLine(Row(composedResult, ComposedTargetMs));

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"one-hop vs relational baseline {RelationalBaselineP95Ms:F3} ms p95: "
            + $"{oneHopResult.P95Ms:F3} ms ({oneHopResult.P95Ms / RelationalBaselineP95Ms:F1}x) — "
            + $"{(oneHopResult.P95Ms <= RelationalBaselineP95Ms ? "no regression" : "REGRESSION against the baseline")}");

        report.AppendLine();
        AppendPlan(report, "depth-3 bounded path, filtered by relation type", depthThreePlan);
        AppendPlan(report, "one-hop reverse lookup", oneHopPlan);
        AppendPlan(report, "composed traversal plus relational filter", composedPlan);

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());

        // Absolute targets. The baseline comparison is reported above and adjudicated in the HLD, not
        // asserted here — NFR-02 requires the shortfall and its cause to be recorded before tuning.
        depthThreeResult.P95Ms.ShouldBeLessThan(DepthThreeTargetMs);
        oneHopResult.P95Ms.ShouldBeLessThan(OneHopTargetMs);
        composedResult.P95Ms.ShouldBeLessThan(ComposedTargetMs);

        // Access path, not just wall clock. NFR-02's criterion is edge storage, so no shape may
        // sequentially scan LINKS; path expansion must go through AGE's traversal function.
        foreach (string plan in new[] { depthThreePlan, composedPlan })
        {
            plan.ShouldContain("age_vle");
        }

        foreach (string plan in new[] { depthThreePlan, oneHopPlan, composedPlan })
        {
            plan.ShouldNotContain("Seq Scan on \"LINKS\"");
        }

        // Where both endpoints are known the vertex anchors must both be indexed. The open-ended
        // composed shape is deliberately excluded: asking for everything reachable has no second anchor
        // to index, and its cost is bounded by the depth limit instead.
        foreach (string plan in new[] { depthThreePlan, oneHopPlan })
        {
            plan.ShouldNotContain("Seq Scan on \"Memory\"");
        }
    }

    private sealed record Measurement(string Name, double P50Ms, double P95Ms, int Results);

    private static string Row(Measurement m, double targetMs) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"| {m.Name} | {m.P50Ms:F3} | {m.P95Ms:F3} | {targetMs:F0} | {(m.P95Ms <= targetMs ? "met" : "MISSED")} |");

    private static void AppendPlan(StringBuilder report, string name, string plan)
    {
        report.AppendLine(CultureInfo.InvariantCulture, $"### plan — {name}");
        report.AppendLine("```");
        report.AppendLine(plan);
        report.AppendLine("```");
    }

    private static async Task<Measurement> MeasureAsync<T>(string name, Func<Task<IReadOnlyList<T>>> run)
    {
        int results = 0;
        for (int i = 0; i < WarmupIterations; i++)
        {
            results = (await run()).Count;
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
        return new Measurement(name, Percentile(samples, 0.50), Percentile(samples, 0.95), results);
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

    private async Task<string> ExplainTraversalAsync(MemoryPathQuery query)
    {
        await using NpgsqlCommand command = await new NpgsqlMemoryTraversal(Db).CreateCommandAsync(query, Ct);
        command.CommandText = $"EXPLAIN (FORMAT TEXT) {command.CommandText}";
        return await ReadPlanAsync(command);
    }

    private async Task<string> ExplainOneHopAsync(Guid uuid)
    {
        await using NpgsqlCommand command = await Graph.CreateCommandAsync(
            NpgsqlMemoryGraph.ListTouchingCypher(uuid),
            "(s agtype, t agtype, r agtype, reason agtype)",
            Ct);
        command.CommandText = $"EXPLAIN (FORMAT TEXT) {command.CommandText}";
        return await ReadPlanAsync(command);
    }

    private async Task<string> ReadPlanAsync(NpgsqlCommand command)
    {
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Finds an endpoint pair with a non-empty depth-3 path on the majority relation, so the measured
    /// shape is a real answer rather than an empty scan.
    /// </summary>
    private async Task<(Guid Source, Guid Target)> FindDepthThreePairAsync(Guid[] uuids)
    {
        for (int i = 0; i + 3 < uuids.Length; i++)
        {
            var probe = new MemoryPathQuery
            {
                SourceUuid = uuids[i],
                TargetUuid = uuids[i + 3],
                MaxDepth = 3,
                Relation = MemoryRelation.RelatesTo,
            };
            if ((await Traversal.FindPathsAsync(probe, Ct)).Count > 0)
            {
                return (uuids[i], uuids[i + 3]);
            }
        }

        throw new InvalidOperationException("The seeded graph holds no depth-3 relates_to path.");
    }

    private async Task<string> RelationDistributionAsync()
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT ag_catalog.agtype_access_operator(
                       VARIADIC ARRAY[properties, '"relation"'::ag_catalog.agtype])::text AS relation,
                   count(*)
            FROM {AgeSession.GraphName}."{AgeSession.EdgeLabel}"
            GROUP BY 1
            ORDER BY 2 DESC;
            """,
            conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(Ct);
        var parts = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            parts.Add($"{reader.GetString(0).Trim('"')}={reader.GetInt64(1)}");
        }

        return "relation distribution: " + string.Join(", ", parts);
    }

    private async Task<Guid[]> SeedAsync()
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);

        await using (var relational = new NpgsqlCommand(
            $"""
            INSERT INTO memory_group (uuid, scope_dimension, initiative_id, tickets, created_on)
            VALUES (gen_random_uuid(), 'product', 1, '[]'::jsonb, now());

            INSERT INTO memory (uuid, lineage_id, group_id, name, description, subject_slug, tags, facets)
            SELECT gen_random_uuid(), gen_random_uuid(),
                   (SELECT max(id) FROM memory_group),
                   'bench-' || g, 'bench subject ' || g, 'bench-subject-' || g, ARRAY[]::text[], ARRAY[]::text[]
            FROM generate_series(1, {MemoryCount}) AS g;

            INSERT INTO memory_version
                (memory_id, version, is_current, statement, content_summary, blob_address, kind,
                 confidence, status, sources, valid_from, created_on)
            SELECT m.id, 1, true, 'bench claim ' || m.id, 'bench summary ' || m.id, NULL,
                   CASE WHEN m.id % 4 = 0 THEN 'decision' ELSE 'reference' END,
                   80, 'approved', '[]'::jsonb, now() - interval '30 days', now()
            FROM memory m;
            """,
            conn))
        {
            await relational.ExecuteNonQueryAsync(Ct);
        }

        // Vertices and edges are created server-side: 13,000 client round trips through the write path
        // would dominate the seed, and the shape produced is identical.
        await using (var vertices = new NpgsqlCommand(
            $$"""
            DO $seed_vertices$
            DECLARE r record;
            BEGIN
                FOR r IN SELECT uuid FROM memory LOOP
                    EXECUTE format(
                        $q$SELECT v FROM ag_catalog.cypher('{{AgeSession.GraphName}}', $c$
                            CREATE (:{{AgeSession.VertexLabel}} {memory_uuid: %L}) RETURN 1
                        $c$) AS (v agtype)$q$, r.uuid::text);
                END LOOP;
            END
            $seed_vertices$;
            """,
            conn))
        {
            vertices.CommandTimeout = 600;
            await vertices.ExecuteNonQueryAsync(Ct);
        }

        await using (var edges = new NpgsqlCommand(
            $$"""
            DO $seed_edges$
            DECLARE r record;
            BEGIN
                FOR r IN
                    WITH ordered AS (SELECT uuid, row_number() OVER (ORDER BY id) AS rn FROM memory)
                    SELECT s.uuid AS source_uuid,
                           t.uuid AS target_uuid,
                           CASE
                               WHEN g % 20 = 0 THEN 'contradicts'
                               WHEN g % 5 = 0 THEN 'depends_on'
                               WHEN g % 7 = 0 THEN 'supersedes'
                               WHEN g % 11 = 0 THEN 'implements'
                               ELSE 'relates_to'
                           END AS relation,
                           'bench seed ' || g AS reason
                    FROM generate_series(0, {{RelationshipCount - 1}}) AS g
                    JOIN ordered s ON s.rn = (g % {{MemoryCount}}) + 1
                    JOIN ordered t ON t.rn = ((g + 1 + (g / {{MemoryCount}})) % {{MemoryCount}}) + 1
                LOOP
                    EXECUTE format(
                        $q$SELECT v FROM ag_catalog.cypher('{{AgeSession.GraphName}}', $c$
                            MATCH (s:{{AgeSession.VertexLabel}}), (t:{{AgeSession.VertexLabel}})
                            WHERE s.memory_uuid = %L AND t.memory_uuid = %L
                            CREATE (s)-[:{{AgeSession.EdgeLabel}} {relation: %L, reason: %L}]->(t)
                            RETURN 1
                        $c$) AS (v agtype)$q$,
                        r.source_uuid::text, r.target_uuid::text, r.relation, r.reason);
                END LOOP;
            END
            $seed_edges$;
            """,
            conn))
        {
            edges.CommandTimeout = 900;
            await edges.ExecuteNonQueryAsync(Ct);
        }

        await using (var analyze = new NpgsqlCommand(
            $"""
            ANALYZE memory;
            ANALYZE memory_version;
            ANALYZE memory_group;
            ANALYZE {AgeSession.GraphName}."{AgeSession.VertexLabel}";
            ANALYZE {AgeSession.GraphName}."{AgeSession.EdgeLabel}";
            """,
            conn))
        {
            await analyze.ExecuteNonQueryAsync(Ct);
        }

        await using (var read = new NpgsqlCommand("SELECT uuid FROM memory ORDER BY id;", conn))
        await using (NpgsqlDataReader reader = await read.ExecuteReaderAsync(Ct))
        {
            var uuids = new List<Guid>(MemoryCount);
            while (await reader.ReadAsync(Ct))
            {
                uuids.Add(reader.GetGuid(0));
            }

            return [.. uuids];
        }
    }
}
