using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

public sealed class MemoryVersionTests : PersistenceTestBase
{
    public MemoryVersionTests(AspireFixture aspire) : base(aspire) { }

    /// <summary>
    /// Bumps the current version: flips the old version's <c>is_current</c> off, then inserts the
    /// new current. The trigger permits only the pointer transfer, so the flip is a legitimate
    /// history mutation. The partial unique index on <c>is_current</c> allows exactly one current,
    /// so the flip must land before the insert — the ordering here is explicit.
    /// </summary>
    private async Task BumpVersionAsync(MemoryVersion oldCurrent, MemoryVersion newVersion)
    {
        oldCurrent.IsCurrent = false;
        await Db.SaveChangesAsync(Ct);

        Db.MemoryVersions.Add(newVersion);
        await Db.SaveChangesAsync(Ct);
    }

    private async Task<(MemoryGroup Group, Memory Memory)> CreateMemoryWithGroupAsync(
        string? description = null, List<string>? tags = null, List<string>? facets = null)
    {
        var group = TestEntities.NewGroup();
        var memory = TestEntities.NewMemory(0, "A memory", description ?? "The subject", subjectSlug: Slug.Subject(description ?? "The subject"));
        memory.Tags = tags ?? [];
        memory.Facets = facets ?? [];

        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        memory.GroupId = group.Id;
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);

        return (group, memory);
    }

    [Fact]
    public async Task VersionChain_ExactlyOneCurrent_AndPreviousRemainsReadable()
    {
        var (_, memory) = await CreateMemoryWithGroupAsync();

        var v1 = TestEntities.NewVersion(memory.Id, 1, "First claim", isCurrent: true);
        Db.MemoryVersions.Add(v1);
        await Db.SaveChangesAsync(Ct);

        var v2 = TestEntities.NewVersion(memory.Id, 2, "Second claim", isCurrent: true);
        await BumpVersionAsync(v1, v2);

        var versions = await Db.MemoryVersions
            .Where(v => v.MemoryId == memory.Id)
            .OrderBy(v => v.Version)
            .ToListAsync(Ct);

        versions.Count.ShouldBe(2);
        versions.Count(v => v.IsCurrent).ShouldBe(1);
        versions.Single(v => v.IsCurrent).Version.ShouldBe(2);
        // v1 remains readable even though superseded.
        versions.Single(v => v.Version == 1).Statement.ShouldBe("First claim");
    }

    [Fact]
    public async Task PartialUniqueIndex_RejectsTwoCurrentVersions()
    {
        var (_, memory) = await CreateMemoryWithGroupAsync();

        Db.MemoryVersions.Add(TestEntities.NewVersion(memory.Id, 1, "First", isCurrent: true));
        Db.MemoryVersions.Add(TestEntities.NewVersion(memory.Id, 2, "Second", isCurrent: true));

        await Should.ThrowAsync<DbUpdateException>(() => Db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Tags_SurviveVersionBump_UnchangedNotDuplicated()
    {
        var (_, memory) = await CreateMemoryWithGroupAsync(tags: ["postgres", "ef-core"], facets: ["storage"]);

        var v1 = TestEntities.NewVersion(memory.Id, 1, "Claim one", isCurrent: true);
        Db.MemoryVersions.Add(v1);
        await Db.SaveChangesAsync(Ct);

        // Bump the version — tags/facets are untouched.
        var v2 = TestEntities.NewVersion(memory.Id, 2, "Claim two", isCurrent: true);
        await BumpVersionAsync(v1, v2);

        var loaded = await Db.Memories.AsNoTracking().SingleAsync(m => m.Id == memory.Id, Ct);

        loaded.Tags.ShouldBe(["postgres", "ef-core"]);
        loaded.Facets.ShouldBe(["storage"]);
        (await Db.MemoryVersions.CountAsync(v => v.MemoryId == memory.Id, Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Facets_BehaveIdenticallyToTags_AcrossBump()
    {
        var (_, memory) = await CreateMemoryWithGroupAsync(facets: ["security", "governance"]);

        var v1 = TestEntities.NewVersion(memory.Id, 1, "One", isCurrent: true);
        Db.MemoryVersions.Add(v1);
        await Db.SaveChangesAsync(Ct);

        var v2 = TestEntities.NewVersion(memory.Id, 2, "Two", isCurrent: true);
        await BumpVersionAsync(v1, v2);

        var loaded = await Db.Memories.AsNoTracking().SingleAsync(m => m.Id == memory.Id, Ct);
        loaded.Facets.ShouldBe(["security", "governance"]);
    }

    [Fact]
    public async Task Facet_NotInRegistry_IsAccepted()
    {
        var (_, memory) = await CreateMemoryWithGroupAsync(facets: ["brand-new-facet"]);

        var v1 = TestEntities.NewVersion(memory.Id, 1, "A claim", isCurrent: true);
        Db.MemoryVersions.Add(v1);

        // Registry is advisory; a facet absent from the seeded labels must be accepted.
        await Db.SaveChangesAsync(Ct);

        Db.Memories.AsNoTracking().Single(m => m.Id == memory.Id).Facets.ShouldBe(["brand-new-facet"]);
    }

    [Fact]
    public async Task Bitemporality_TwoAxesMoveIndependently()
    {
        var (_, memory) = await CreateMemoryWithGroupAsync();

        var v1 = TestEntities.NewVersion(memory.Id, 1, "Claim", isCurrent: true,
            validFrom: DateTimeOffset.UtcNow.AddMonths(-3), validUntil: DateTimeOffset.UtcNow.AddMonths(-1),
            sources: [SourceDocument.Create("ticket", "ACM-1")]);
        v1.CreatedOn = DateTimeOffset.UtcNow.AddDays(-1);

        Db.MemoryVersions.Add(v1);
        await Db.SaveChangesAsync(Ct);

        var loaded = await Db.MemoryVersions.AsNoTracking().SingleAsync(v => v.Id == v1.Id, Ct);

        // Business time (valid_until closed) and system time (created_on) are independent axes.
        // Compare within a tolerance: timestamptz stores microsecond precision, so the round-trip
        // drops sub-microsecond ticks and exact equality is not meaningful here.
        var tolerance = TimeSpan.FromSeconds(1);
        loaded.ValidUntil.ShouldNotBeNull();
        (loaded.ValidFrom - v1.ValidFrom).Duration().ShouldBeLessThanOrEqualTo(tolerance);
        (loaded.ValidUntil!.Value - v1.ValidUntil!.Value).Duration().ShouldBeLessThanOrEqualTo(tolerance);
        (loaded.CreatedOn - v1.CreatedOn).Duration().ShouldBeLessThanOrEqualTo(tolerance);
    }

    [Fact]
    public async Task Sources_RoundTripAsJsonb_AndDifferAcrossVersions()
    {
        var (_, memory) = await CreateMemoryWithGroupAsync();

        var v1 = TestEntities.NewVersion(memory.Id, 1, "One", isCurrent: true,
            sources: [SourceDocument.Create("ticket", "ACM-1", DateTimeOffset.UtcNow.AddDays(-2))]);
        Db.MemoryVersions.Add(v1);
        await Db.SaveChangesAsync(Ct);

        var v2Sources = new List<SourceDocument>
        {
            SourceDocument.Create("ticket", "ACM-2", DateTimeOffset.UtcNow.AddDays(-1)),
            SourceDocument.Create("conversation", "session-9", DateTimeOffset.UtcNow),
        };
        var v2 = TestEntities.NewVersion(memory.Id, 2, "Two", isCurrent: true, sources: v2Sources);
        await BumpVersionAsync(v1, v2);

        var versions = await Db.MemoryVersions.AsNoTracking()
            .Where(v => v.MemoryId == memory.Id)
            .OrderBy(v => v.Version)
            .ToListAsync(Ct);

        versions[0].Sources.Single().Reference.ShouldBe("ACM-1");
        versions[1].Sources.Count.ShouldBe(2);
        versions[1].Sources[0].Reference.ShouldBe("ACM-2");
        versions[1].Sources[1].Reference.ShouldBe("session-9");
        versions[0].Sources.All(s => s.V == JsonShapeDocument.CurrentShapeVersion).ShouldBeTrue();
        versions[1].Sources.All(s => s.V == JsonShapeDocument.CurrentShapeVersion).ShouldBeTrue();
    }
}
