using System.Globalization;
using System.Text;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// L1 evidence that the "any" facet/tag match uses the GIN index rather than a sequential scan. The
/// Application component tests already prove the union/containment semantics against the real provider;
/// this one proves the index serves the overlap predicate, so the fix does not trade correctness for a
/// scan.
/// </summary>
public sealed class FacetMatchIndexTests : PersistenceTestBase
{
    public FacetMatchIndexTests(AspireFixture aspire) : base(aspire) { }

    private NpgsqlMemorySearch Search => new(Db);

    [Fact]
    public async Task Any_overlap_union_is_served_by_the_gin_index()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var storage = TestEntities.NewMemory(group.Id, "M1", "Storage");
        storage.Facets = ["storage"];
        Db.Memories.Add(storage);
        await Db.SaveChangesAsync(Ct);
        Db.MemoryVersions.Add(TestEntities.NewVersion(storage.Id, 1, "We chose PostgreSQL"));

        var domain = TestEntities.NewMemory(group.Id, "M2", "Domain model");
        domain.Facets = ["domain-model"];
        Db.Memories.Add(domain);
        await Db.SaveChangesAsync(Ct);
        Db.MemoryVersions.Add(TestEntities.NewVersion(domain.Id, 1, "Memory is one atomic fact"));

        await Db.SaveChangesAsync(Ct);

        var criteria = new MemorySearchCriteria
        {
            Facets = ["storage", "domain-model"],
            FacetMatchMode = FacetMatchModeValue.Any,
        };

        var rows = await Search.SearchAsync(criteria, Ct);
        rows.Select(r => r.Name).ShouldBe(["M1", "M2"], ignoreOrder: true);

        // The overlap predicate the "any" mode emits must be served by the facet GIN index, not a scan.
        string plan = await ExplainAsync("&&", ["storage", "domain-model"]);
        plan.ShouldContain("IX_memory_facets");
        plan.ShouldNotContain("Seq Scan");
    }

    [Fact]
    public async Task All_containment_is_served_by_the_gin_index()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var m = TestEntities.NewMemory(group.Id, "M1", "Multi facet");
        m.Facets = ["architecture", "storage"];
        Db.Memories.Add(m);
        await Db.SaveChangesAsync(Ct);
        Db.MemoryVersions.Add(TestEntities.NewVersion(m.Id, 1, "Claim"));

        await Db.SaveChangesAsync(Ct);

        string plan = await ExplainAsync("@>", ["architecture", "storage"]);
        plan.ShouldContain("IX_memory_facets");
        plan.ShouldNotContain("Seq Scan");
    }

    /// <summary>
    /// EXPLAIN of the same shape <see cref="NpgsqlMemorySearch"/> emits: the array is a bound
    /// parameter, not a literal, so a parameterized-plan regression (operator/type mismatch dropping
    /// the GIN path) is caught rather than hidden by a hand-written literal.
    /// </summary>
    private async Task<string> ExplainAsync(string matchOperator, string[] values)
    {
        await using var conn = await DataSource.OpenConnectionAsync(Ct);
        // SET LOCAL scopes the planner override to this transaction, so the pooled connection is
        // clean on return. enable_seqscan off forces the planner to prove the index path rather than
        // default to a scan over a tiny fixture table, where a seq scan is honestly optimal but
        // hides the regression.
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await using (var setCmd = new NpgsqlCommand("SET LOCAL enable_seqscan = off", conn, tx))
        {
            await setCmd.ExecuteNonQueryAsync(Ct);
        }

        string sql = $"EXPLAIN (FORMAT TEXT) SELECT * FROM memory WHERE facets {matchOperator} $1";
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue(values);
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
}
