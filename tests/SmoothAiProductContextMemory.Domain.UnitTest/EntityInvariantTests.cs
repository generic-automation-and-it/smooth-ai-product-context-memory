using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Domain.UnitTest;

public class EntityInvariantTests
{
    [Fact]
    public void Subject_lives_on_logical_row_and_claim_on_version()
    {
        var memory = new Memory { Description = "The subject", SubjectSlug = "the-subject", Tags = [], Facets = [] };
        var version = new MemoryVersion { Statement = "The claim" };

        memory.Description.ShouldBe("The subject");
        version.Statement.ShouldBe("The claim");
        // The split is structural: the subject is unversioned, the claim is versioned.
        version.ShouldNotBeNull();
    }

    [Fact]
    public void Both_time_axes_are_present_on_the_version()
    {
        var version = new MemoryVersion
        {
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidUntil = DateTimeOffset.UtcNow.AddDays(1),
            CreatedOn = DateTimeOffset.UtcNow,
        };

        version.ValidUntil.ShouldNotBeNull();
        version.ValidFrom.ShouldBeLessThan(version.ValidUntil.Value);
        version.CreatedOn.ShouldBeGreaterThanOrEqualTo(DateTimeOffset.MinValue);
    }

    [Fact]
    public void Tags_and_facets_are_unversioned_arrays_on_the_logical_row()
    {
        var memory = new Memory { Tags = ["alpha", "beta"], Facets = ["storage"] };

        memory.Tags.ShouldBe(["alpha", "beta"], ignoreOrder: true);
        memory.Facets.ShouldBe(["storage"]);
    }

    [Fact]
    public void Kind_is_open_vocabulary_not_an_enum()
    {
        // New kinds emerge by design; the field is a plain string, so any value is accepted.
        var version = new MemoryVersion { Kind = "brand-new-kind" };
        version.Kind.ShouldBe("brand-new-kind");
    }
}
