using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.UnitTest;

public class ModelShapeGuardTests
{
    private static readonly Type[] ExpectedEntityTypes =
    [
        typeof(Initiative),
        typeof(Label),
        typeof(MemoryGroup),
        typeof(GroupDescription),
        typeof(Memory),
        typeof(MemoryVersion),
        typeof(MemoryLink),
    ];

    [Fact]
    public void DbContext_exposes_exactly_the_seven_entity_types()
    {
        var options = new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
            .UseNpgsql("Host=localhost;Database=throwaway;Username=x;Password=y")
            .Options;

        using var db = new SmoothAiProductContextMemoryDbContext(options);
        Type[] actual = db.Model.GetEntityTypes()
            .Select(t => t.ClrType)
            .OrderBy(t => t.Name)
            .ToArray();

        Type[] expected = ExpectedEntityTypes.OrderBy(t => t.Name).ToArray();

        actual.ShouldBe(expected);

        // Deliberately a literal set, not a count: reintroducing a table for tags, facets,
        // sources, repositories or tickets breaks the build rather than passing review unnoticed.
        ExpectedEntityTypes.Length.ShouldBe(7);
    }
}
