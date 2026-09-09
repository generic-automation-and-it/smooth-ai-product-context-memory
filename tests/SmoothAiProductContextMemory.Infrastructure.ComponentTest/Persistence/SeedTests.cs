using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

public sealed class SeedTests : PersistenceTestBase
{
    public SeedTests(AspireFixture aspire) : base(aspire) { }

    [Fact]
    public async Task ToBeDecidedInitiative_IsSeeded()
    {
        await Db.Initiatives.SingleAsync(i => i.Name == "to-be-decided", Ct);
    }

    [Fact]
    public async Task TenControlledFacets_AreSeeded()
    {
        string[] expected =
        [
            "positioning", "architecture", "storage", "domain-model", "write-path",
            "retrieval", "security", "governance", "prior-art", "process",
        ];

        var names = await Db.Labels.Select(l => l.Name).ToListAsync(Ct);

        names.OrderBy(n => n).ShouldBe(expected.OrderBy(n => n));
        expected.Length.ShouldBe(10);
    }
}
