using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Retrieval;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

public sealed class TicketTraversalBenchmarkTests(AspireFixture aspire) : PersistenceTestBase(aspire)
{
    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    public async Task HubActiveComposedTraversal_P95Within100Ms_WithActualCommandPlan(int fanout)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SMOOTH_AGE_BENCH") == "1",
            "Set SMOOTH_AGE_BENCH=1 to record ticket traversal measurements.");

        var graph = new NpgsqlTicketGraph(Db);
        var groups = new List<MemoryGroup>();
        var declarations = new List<(TicketIdentity Parent, TicketIdentity Child)>();
        TicketIdentity root = AddGroup("root", "product");
        for (int i = 0; i < fanout; i++)
        {
            TicketIdentity child = AddGroup($"child-{i:D4}", i % 5 == 0 ? "program" : i % 3 == 0 ? "customer" : "product");
            declarations.Add((root, child));
            TicketIdentity grandchild = AddGroup($"grandchild-{i:D4}", "product");
            declarations.Add((child, grandchild));
            TicketIdentity leaf = AddGroup($"leaf-{i:D4}", i % 7 == 0 ? "program" : "product");
            declarations.Add((grandchild, leaf));
        }

        Stopwatch seed = Stopwatch.StartNew();
        Db.MemoryGroups.AddRange(groups);
        await Db.SaveChangesAsync(Ct);
        var memories = groups.SelectMany(g => Enumerable.Range(0, 3).Select(i =>
            TestEntities.NewMemory(g.Id, $"Bench {i}", $"Bench subject {i}"))).ToArray();
        Db.Memories.AddRange(memories);
        await Db.SaveChangesAsync(Ct);
        Db.MemoryVersions.AddRange(memories.Select((m, i) => TestEntities.NewVersion(m.Id, 1, "Benchmark claim",
            kind: i % 3 == 0 ? "reference" : "decision")));
        await Db.SaveChangesAsync(Ct);
        await using (var transaction = await Db.Database.BeginTransactionAsync(Ct))
        {
            foreach ((TicketIdentity parent, TicketIdentity child) in declarations)
                await graph.ChangeParentAsync(new TicketParentChange(child, parent, null, "Benchmark declaration", "practitioner"), Ct);
            await transaction.CommitAsync(Ct);
        }

        await Db.Database.ExecuteSqlRawAsync("""
            ANALYZE public.memory_group; ANALYZE public.memory; ANALYZE public.memory_version;
            ANALYZE memory_graph."Ticket"; ANALYZE memory_graph."TICKET_PARENT";
            """, Ct);
        seed.Stop();

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"fanout={fanout}; groups/tickets={groups.Count}; edges={declarations.Count}; memories={memories.Length}; seed={seed.Elapsed.TotalSeconds:F1}s");
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        report.Clear();
        var measurements = new List<double>();
        foreach (int depth in new[] { 2, 3, 5 })
        {
            var query = new TicketTraversalQuery
            {
                Anchor = root, MaxDepth = depth, PathLimit = 200, MemoryLimit = 200, Kind = "decision",
                RequiredScopeDimension = MemoryScopeFilter.Plan("product", false).RequiredDimension,
                HiddenDimensions = MemoryScopeFilter.HiddenDimensions("product", false),
            };
            TicketTraversalResult result = await graph.TraverseAsync(query, Ct);
            result.Paths.ShouldNotBeEmpty();
            // At large fanouts the response cap selects depth-one paths, but the actual walk still
            // expands every admitted branch to depth two/three and probes one further visible hop.
            result.Paths.Count.ShouldBe(Math.Min(200, depth == 2 ? fanout * 8 / 5 : fanout * 8 / 5 + Enumerable.Range(0, fanout).Count(i => i % 5 != 0 && i % 7 != 0)));
            result.Items.Count.ShouldBe(200);
            result.Items.ShouldAllBe(m => m.ScopeDimension == "product" && m.Kind == "decision");
            result.Paths.SelectMany(p => p.Hops).ShouldNotContain(h =>
                h.Child.Key.StartsWith("child-", StringComparison.Ordinal) && int.Parse(h.Child.Key.Substring(6), CultureInfo.InvariantCulture) % 5 == 0);
            result.Disclosure.DepthLimitReached.ShouldBe(depth == 2);
            result.Disclosure.MemoryLimitReached.ShouldBeTrue();

            for (int i = 0; i < 20; i++) await graph.TraverseAsync(query, Ct);
            var samples = new double[100];
            for (int i = 0; i < samples.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                await graph.TraverseAsync(query, Ct);
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            Array.Sort(samples);
            double p95 = samples[94] * 0.95 + samples[95] * 0.05;
            measurements.Add(p95);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"depth={depth}; warmups=20; samples=100; p50={(samples[49] + samples[50]) / 2:F3}ms; p95={p95:F3}ms; target<=100ms; paths={result.Paths.Count}; memories={result.Items.Count}; caps={result.Disclosure}");
            await using NpgsqlCommand command = await graph.CreateTraversalCommandAsync(query, Ct);
            report.AppendLine("Actual provider command:").AppendLine(command.CommandText);
            report.AppendLine("EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT):");
            command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + command.CommandText;
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct)) report.AppendLine(reader.GetString(0));
            TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
            report.Clear();
        }

        measurements.ShouldAllBe(p95 => p95 <= 100, "actual composed ticket traversal must meet p95 <= 100 ms");

        TicketIdentity AddGroup(string key, string scope)
        {
            groups.Add(TestEntities.NewGroup(scope, tickets: [TicketDocument.Create("jira", key, "https://tracker/bench")]));
            return new TicketIdentity("jira", key);
        }
    }
}
