using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Features.Groups;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class AppendGroupDescriptionValidatorTests
{
    private readonly AppendGroupDescription.Validator _validator = new();

    [Fact]
    public void Requires_nonempty_group_uuid()
    {
        _validator.TestValidate(new AppendGroupDescription.Request(Guid.Empty, "n", "b"))
            .ShouldHaveValidationErrorFor(x => x.GroupUuid);
    }

    [Fact]
    public void Requires_nonempty_name()
    {
        _validator.TestValidate(new AppendGroupDescription.Request(Guid.NewGuid(), " ", "b"))
            .ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Requires_nonempty_body()
    {
        _validator.TestValidate(new AppendGroupDescription.Request(Guid.NewGuid(), "n", " "))
            .ShouldHaveValidationErrorFor(x => x.Body);
    }

    [Fact]
    public void Accepts_valid_request()
    {
        _validator.TestValidate(new AppendGroupDescription.Request(Guid.NewGuid(), "n", "body"))
            .ShouldNotHaveAnyValidationErrors();
    }
}
