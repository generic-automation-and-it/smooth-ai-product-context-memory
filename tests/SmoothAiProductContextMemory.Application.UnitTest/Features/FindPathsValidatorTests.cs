using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.Links;
using SmoothAiProductContextMemory.Domain;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class FindPathsValidatorTests
{
    private readonly FindPaths.Validator _validator = new();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MemoryTraversalDefaults.MaxDepth + 1)]
    public void Rejects_depth_outside_the_bound(int maxDepth)
    {
        var result = _validator.TestValidate(Request() with { MaxDepth = maxDepth });
        result.ShouldHaveValidationErrorFor(x => x.MaxDepth);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(MemoryTraversalDefaults.MaxDepth)]
    public void Accepts_depth_within_the_bound(int maxDepth)
    {
        var result = _validator.TestValidate(Request() with { MaxDepth = maxDepth });
        result.ShouldNotHaveValidationErrorFor(x => x.MaxDepth);
    }

    [Fact]
    public void Rejects_empty_source()
    {
        var result = _validator.TestValidate(Request() with { SourceUuid = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.SourceUuid);
    }

    [Fact]
    public void Rejects_unknown_direction()
    {
        var result = _validator.TestValidate(Request() with { Direction = "sideways" });
        result.ShouldHaveValidationErrorFor(x => x.Direction);
    }

    [Theory]
    [InlineData(FindPaths.DirectionValue.Outbound)]
    [InlineData(FindPaths.DirectionValue.Inbound)]
    [InlineData(FindPaths.DirectionValue.Either)]
    [InlineData(null)]
    public void Accepts_known_direction_or_absence(string? direction)
    {
        var result = _validator.TestValidate(Request() with { Direction = direction });
        result.ShouldNotHaveValidationErrorFor(x => x.Direction);
    }

    [Fact]
    public void Rejects_limit_above_the_read_cap()
    {
        var result = _validator.TestValidate(Request() with { Limit = MemorySearchDefaults.MaxLimit + 1 });
        result.ShouldHaveValidationErrorFor(x => x.Limit);
    }

    [Fact]
    public void Accepts_a_bounded_request()
    {
        var result = _validator.TestValidate(Request());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Accepts_a_self_referential_endpoint_pair()
    {
        // A cycle back to the source is a legitimate provenance answer, unlike a self-link on write.
        Guid id = Guid.NewGuid();
        var result = _validator.TestValidate(Request() with { SourceUuid = id, TargetUuid = id });
        result.ShouldNotHaveAnyValidationErrors();
    }

    private static FindPaths.Request Request() =>
        new(Guid.NewGuid(), MaxDepth: 3, Relation: MemoryRelation.DependsOn);
}
