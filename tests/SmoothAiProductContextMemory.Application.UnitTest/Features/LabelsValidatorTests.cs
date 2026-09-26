using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Features.Labels;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class ProposeLabelValidatorTests
{
    private readonly ProposeLabel.Validator _validator = new();

    [Fact]
    public void Requires_nonempty_name()
    {
        _validator.TestValidate(new ProposeLabel.Request(" ")).ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Rejects_name_over_100_characters()
    {
        _validator.TestValidate(new ProposeLabel.Request(new string('a', 101)))
            .ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Accepts_plain_name()
    {
        _validator.TestValidate(new ProposeLabel.Request("steady-context")).ShouldNotHaveAnyValidationErrors();
    }
}

public class GetLabelsValidatorTests
{
    private readonly GetLabels.Validator _validator = new();

    [Fact]
    public void Accepts_empty_request()
    {
        _validator.TestValidate(new GetLabels.Request()).ShouldNotHaveAnyValidationErrors();
    }
}
