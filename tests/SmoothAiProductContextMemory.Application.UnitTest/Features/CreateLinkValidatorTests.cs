using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Features.Links;
using SmoothAiProductContextMemory.Domain;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class CreateLinkValidatorTests
{
    private readonly CreateLink.Validator _validator = new();

    [Fact]
    public void Rejects_self_link()
    {
        Guid id = Guid.NewGuid();
        var result = _validator.TestValidate(
            new CreateLink.Request(id, id, MemoryRelation.RelatesTo, "because"));
        result.ShouldHaveValidationErrorFor(x => x);
    }

    [Fact]
    public void Accepts_distinct_link()
    {
        var result = _validator.TestValidate(
            new CreateLink.Request(
                Guid.NewGuid(),
                Guid.NewGuid(),
                MemoryRelation.DependsOn,
                "needed"));
        result.ShouldNotHaveAnyValidationErrors();
    }
}
