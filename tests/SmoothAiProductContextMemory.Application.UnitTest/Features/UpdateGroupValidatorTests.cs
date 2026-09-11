using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Features.Groups;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class UpdateGroupValidatorTests
{
    private readonly UpdateGroup.Validator _validator = new();

    [Fact]
    public void Rejects_empty_group_and_unknown_scope()
    {
        UpdateGroup.Request request = new(Guid.Empty, null, null, null, "galaxy", null);

        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.GroupUuid);
        result.ShouldHaveValidationErrorFor(x => x.ScopeDimension);
    }

    [Fact]
    public void Accepts_a_partial_update()
    {
        UpdateGroup.Request request = new(Guid.NewGuid(), "kingstown", null, null, null, null);

        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Accepts_a_scope_change_with_an_identifier()
    {
        UpdateGroup.Request request = new(
            Guid.NewGuid(),
            null,
            null,
            null,
            MemoryGroup.ScopeDimensionValue.Program,
            "atlas");

        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }
}
