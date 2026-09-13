using System.Diagnostics;
using Npgsql;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

public sealed class AgeOneHopBaselineTests : PersistenceTestBase
{
    private const int MemoryCount = 3_000;
    private const int RelationshipCount = 10_000;
    private const int WarmupIterations = 20;
    private const int MeasuredIterations = 100;

    public AgeOneHopBaselineTests(AspireFixture aspire) : base(aspire) { }

    [Fact]
    public async Task OneHopReverseLookup_RelationalBaseline_AtNfr02Volume()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable("SMOOTH_AGE_BASELINE"), "1", StringComparison.Ordinal),
            "Set SMOOTH_AGE_BASELINE=1 to record the NFR-02 one-hop baseline.");

        await SeedAsync();

        long targetId = await ScalarInt64("SELECT id FROM memory ORDER BY id OFFSET 1500 LIMIT 1;");
        string plan = await ScalarText(
            $"""
            EXPLAIN (FORMAT TEXT)
            SELECT source_memory_id, relation, reason
            FROM memory_link
            WHERE target_memory_id = {targetId};
            """);

        for (int i = 0; i < WarmupIterations; i++)
        {
            await ReverseLookupAsync(targetId);
        }

        var samples = new double[MeasuredIterations];
        var stopwatch = new Stopwatch();
        for (int i = 0; i < MeasuredIterations; i++)
        {
            stopwatch.Restart();
            await ReverseLookupAsync(targetId);
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        double p50 = Percentile(samples, 0.50);
        double p95 = Percentile(samples, 0.95);

        TestContext.Current.TestOutputHelper?.WriteLine(
            "one-hop reverse lookup n={0} p50={1:F3}ms p95={2:F3}ms{3}{4}",
            MeasuredIterations,
            p50,
            p95,
            Environment.NewLine,
            plan);

        p95.ShouldBeLessThan(10.0);
        plan.ShouldContain("Index");
        plan.ShouldNotContain("Seq Scan");
    }

    private async Task SeedAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);

        await using (var group = new NpgsqlCommand(
            """
            INSERT INTO memory_group (uuid, scope_dimension, initiative_id, tickets)
            VALUES (gen_random_uuid(), 'product', 1, '[]'::jsonb)
            RETURNING id;
            """,
            conn,
            (NpgsqlTransaction)tx))
        {
            long groupId = Convert.ToInt64(await group.ExecuteScalarAsync(Ct));

            await using var memories = new NpgsqlCommand(
                """
                INSERT INTO memory (uuid, lineage_id, group_id, name, description, subject_slug, tags, facets)
                SELECT gen_random_uuid(), gen_random_uuid(), $1,
                       'm-' || g, 'subject ' || g, 'subject-' || g, '{}', '{}'
                FROM generate_series(1, $2) AS g;
                """,
                conn,
                (NpgsqlTransaction)tx);
            memories.Parameters.AddWithValue(groupId);
            memories.Parameters.AddWithValue(MemoryCount);
            await memories.ExecuteNonQueryAsync(Ct);
        }

        long minId;
        await using (var min = new NpgsqlCommand("SELECT min(id) FROM memory;", conn, (NpgsqlTransaction)tx))
        {
            minId = Convert.ToInt64(await min.ExecuteScalarAsync(Ct));
        }

        await using (var links = new NpgsqlCommand(
            """
            INSERT INTO memory_link (source_memory_id, target_memory_id, relation, reason)
            SELECT
                $3 + (g % $1),
                $3 + ((g + 1 + (g / $1)) % $1),
                CASE
                    WHEN g % 20 = 0 THEN 'contradicts'
                    WHEN g % 5 = 0 THEN 'depends_on'
                    WHEN g % 7 = 0 THEN 'supersedes'
                    WHEN g % 11 = 0 THEN 'implements'
                    ELSE 'relates_to'
                END,
                'baseline seed ' || g
            FROM generate_series(0, $2 - 1) AS g;
            """,
            conn,
            (NpgsqlTransaction)tx))
        {
            links.Parameters.AddWithValue(MemoryCount);
            links.Parameters.AddWithValue(RelationshipCount);
            links.Parameters.AddWithValue(minId);
            await links.ExecuteNonQueryAsync(Ct);
        }

        await tx.CommitAsync(Ct);
    }

    private async Task ReverseLookupAsync(long targetId)
    {
        await using var conn = await DataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT source_memory_id, relation, reason
            FROM memory_link
            WHERE target_memory_id = $1;
            """,
            conn);
        cmd.Parameters.AddWithValue(targetId);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
        }
    }

    private async Task<long> ScalarInt64(string sql)
    {
        await using var conn = await DataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct));
    }

    private async Task<string> ScalarText(string sql)
    {
        await using var conn = await DataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static double Percentile(double[] sortedAscending, double p)
    {
        if (sortedAscending.Length == 0)
        {
            return 0;
        }

        double index = p * (sortedAscending.Length - 1);
        int lo = (int)Math.Floor(index);
        int hi = (int)Math.Ceiling(index);
        if (lo == hi)
        {
            return sortedAscending[lo];
        }

        double weight = index - lo;
        return (sortedAscending[lo] * (1 - weight)) + (sortedAscending[hi] * weight);
    }
}
