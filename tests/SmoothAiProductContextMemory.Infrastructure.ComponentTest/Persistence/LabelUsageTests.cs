using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

public sealed class LabelUsageTests : PersistenceTestBase
{
    public LabelUsageTests(AspireFixture aspire) : base(aspire) { }

    private sealed record LabelUsageRow(string Name, long Uses);

    private async Task<List<LabelUsageRow>> ReadViewAsync()
    {
        var rows = new List<LabelUsageRow>();

        await using var conn = await DataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT name, uses FROM label_usage", conn);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new LabelUsageRow(reader.GetString(0), reader.GetInt64(1)));
        }

        return rows;
    }

    [Fact]
    public async Task LabelUsage_ReturnsCorrectCounts_AndReflectsRemoval()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var m1 = TestEntities.NewMemory(group.Id, "M1", "Subject 1");
        m1.Facets = ["storage", "security"];
        var m2 = TestEntities.NewMemory(group.Id, "M2", "Subject 2");
        m2.Facets = ["storage"];
        var m3 = TestEntities.NewMemory(group.Id, "M3", "Subject 3");
        m3.Facets = ["governance"];
        Db.Memories.AddRange(m1, m2, m3);
        await Db.SaveChangesAsync(Ct);

        var afterInsert = await ReadViewAsync();
        afterInsert.Single(r => r.Name == "storage").Uses.ShouldBe(2);
        afterInsert.Single(r => r.Name == "security").Uses.ShouldBe(1);
        afterInsert.Single(r => r.Name == "governance").Uses.ShouldBe(1);

        // Removing a facet from a memory is a legal update (memory is not append-only).
        m1.Facets = ["security"];
        await Db.SaveChangesAsync(Ct);

        var afterRemoval = await ReadViewAsync();
        afterRemoval.Single(r => r.Name == "storage").Uses.ShouldBe(1);
        afterRemoval.Single(r => r.Name == "security").Uses.ShouldBe(1);
    }
}
